using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ListaryOpen.App.Previewing;

internal sealed class EmailPreviewProvider : IFilePreviewProvider
{
    private const int MaximumInputBytes = 4 * 1024 * 1024;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumParts = 256;
    private const int MaximumDepth = 8;
    private const int MaximumBodyCharacters = 256 * 1024;
    private const int MaximumAttachments = 100;
    private static readonly Regex EncodedWord = new(
        @"=\?(?<charset>[^?\s]+)\?(?<encoding>[bBqQ])\?(?<value>[^?]*)\?=",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    static EmailPreviewProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Email);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent Load(PreviewContext context, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(stream.Length, MaximumInputBytes);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        var source = Encoding.Latin1.GetString(bytes);
        if (context.Extension.Equals(".mbox", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewContent.ForText(
                "Mailbox summary",
                SummarizeMailbox(source, stream.Length > MaximumInputBytes, cancellationToken));
        }
        var state = new ParseState(cancellationToken);
        var root = ParsePart(source, 0, state);
        var headers = root.Headers;

        var builder = new StringBuilder("Internet message").AppendLine().AppendLine();
        AppendHeader(builder, "From", headers);
        AppendHeader(builder, "To", headers);
        AppendHeader(builder, "Cc", headers);
        AppendHeader(builder, "Date", headers);
        AppendHeader(builder, "Subject", headers);
        builder.AppendLine();

        var body = state.PlainBody ?? state.HtmlBody;
        if (!string.IsNullOrWhiteSpace(body))
        {
            builder.AppendLine(body.Length <= MaximumBodyCharacters
                ? body.Trim()
                : body[..MaximumBodyCharacters].Trim() + Environment.NewLine + "… body truncated");
        }
        else
        {
            builder.AppendLine("(No displayable text body)");
        }

        if (state.Attachments.Count > 0)
        {
            builder.AppendLine().Append("Attachments (").Append(state.Attachments.Count).AppendLine("):");
            foreach (var attachment in state.Attachments.Take(MaximumAttachments))
            {
                builder.Append("• ").AppendLine(attachment);
            }
        }

        if (stream.Length > MaximumInputBytes || state.Truncated)
        {
            builder.AppendLine().AppendLine("… message preview truncated by safety limits");
        }

        return PreviewContent.ForText("Safe MIME preview", builder.ToString());
    }

    private static string SummarizeMailbox(
        string source,
        bool inputTruncated,
        CancellationToken cancellationToken)
    {
        const int maximumMessages = 100;
        var builder = new StringBuilder("MBOX mailbox").AppendLine().AppendLine();
        var starts = new List<int>();
        if (source.StartsWith("From ", StringComparison.Ordinal))
        {
            starts.Add(0);
        }
        for (var index = 0; index < source.Length - 6 && starts.Count <= maximumMessages; index++)
        {
            if (source[index] == '\n' && source.AsSpan(index + 1).StartsWith("From "))
            {
                starts.Add(index + 1);
            }
        }
        for (var messageIndex = 0; messageIndex < Math.Min(starts.Count, maximumMessages); messageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = starts[messageIndex];
            var end = messageIndex + 1 < starts.Count ? starts[messageIndex + 1] : source.Length;
            var message = source[start..end];
            var firstLineEnd = message.IndexOf('\n');
            if (firstLineEnd >= 0)
            {
                message = message[(firstLineEnd + 1)..];
            }
            SplitHeaderAndBody(message, out var headerBlock, out _);
            var headers = ParseHeaders(headerBlock.Length <= MaximumHeaderBytes
                ? headerBlock
                : headerBlock[..MaximumHeaderBytes]);
            builder.Append("— Message ").Append(messageIndex + 1).AppendLine(" —");
            AppendHeader(builder, "From", headers);
            AppendHeader(builder, "Date", headers);
            AppendHeader(builder, "Subject", headers);
            builder.AppendLine();
        }
        if (starts.Count == 0)
        {
            return "The file does not contain a recognized MBOX envelope.";
        }
        if (starts.Count > maximumMessages || inputTruncated)
        {
            builder.AppendLine("… mailbox summary truncated by safety limits");
        }
        builder.AppendLine("Message bodies and attachment payloads are not indexed by the mailbox summary.");
        return builder.ToString();
    }

    private static MimePart ParsePart(string source, int depth, ParseState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        if (depth > MaximumDepth || state.PartCount++ >= MaximumParts)
        {
            state.Truncated = true;
            return new MimePart(new(StringComparer.OrdinalIgnoreCase), string.Empty);
        }

        SplitHeaderAndBody(source, out var headerBlock, out var body);
        if (Encoding.Latin1.GetByteCount(headerBlock) > MaximumHeaderBytes)
        {
            headerBlock = headerBlock[..Math.Min(headerBlock.Length, MaximumHeaderBytes)];
            state.Truncated = true;
        }

        var headers = ParseHeaders(headerBlock);
        var contentType = ParseParameterized(headers.GetValueOrDefault("Content-Type") ?? "text/plain");
        var disposition = ParseParameterized(headers.GetValueOrDefault("Content-Disposition") ?? string.Empty);
        var mediaType = contentType.Value.ToLowerInvariant();
        var fileName = disposition.Parameters.GetValueOrDefault("filename") ??
            contentType.Parameters.GetValueOrDefault("name");
        var isAttachment = disposition.Value.Equals("attachment", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(fileName);

        if (isAttachment)
        {
            state.Attachments.Add(DecodeHeader(fileName ?? "(unnamed attachment)"));
            return new MimePart(headers, body);
        }

        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal) &&
            contentType.Parameters.TryGetValue("boundary", out var boundary) &&
            !string.IsNullOrEmpty(boundary))
        {
            foreach (var child in SplitMultipart(body, boundary))
            {
                ParsePart(child, depth + 1, state);
            }
        }
        else if (mediaType is "text/plain" or "text/html")
        {
            var decoded = DecodeBody(body, headers.GetValueOrDefault("Content-Transfer-Encoding"),
                contentType.Parameters.GetValueOrDefault("charset"), state);
            if (mediaType == "text/plain" && state.PlainBody is null)
            {
                state.PlainBody = decoded;
            }
            else if (mediaType == "text/html" && state.HtmlBody is null)
            {
                state.HtmlBody = TextPreviewProvider.ConvertHtmlToText(decoded);
            }
        }

        return new MimePart(headers, body);
    }

    private static Dictionary<string, string> ParseHeaders(string block)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (var line in block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && current is not null)
            {
                headers[current] += " " + line.Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            current = line[..colon].Trim();
            headers[current] = line[(colon + 1)..].Trim();
        }

        return headers;
    }

    private static void AppendHeader(StringBuilder builder, string name, Dictionary<string, string> headers)
    {
        if (headers.TryGetValue(name, out var value))
        {
            builder.Append(name).Append(": ").AppendLine(DecodeHeader(value));
        }
    }

    private static string DecodeHeader(string value) => EncodedWord.Replace(value, match =>
    {
        try
        {
            var bytes = match.Groups["encoding"].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(match.Groups["value"].Value)
                : DecodeQuotedPrintable(match.Groups["value"].Value.Replace('_', ' '));
            return GetEncoding(match.Groups["charset"].Value).GetString(bytes);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return match.Value;
        }
    });

    private static string DecodeBody(string body, string? transferEncoding, string? charset, ParseState state)
    {
        byte[] bytes;
        try
        {
            bytes = transferEncoding?.Trim().ToLowerInvariant() switch
            {
                "base64" => Convert.FromBase64String(Regex.Replace(body, @"\s+", string.Empty,
                    RegexOptions.None, TimeSpan.FromMilliseconds(200))),
                "quoted-printable" => DecodeQuotedPrintable(body),
                _ => Encoding.Latin1.GetBytes(body)
            };
        }
        catch (FormatException)
        {
            state.Truncated = true;
            bytes = Encoding.Latin1.GetBytes(body);
        }

        if (bytes.Length > MaximumBodyCharacters * 4)
        {
            bytes = bytes[..(MaximumBodyCharacters * 4)];
            state.Truncated = true;
        }

        return GetEncoding(charset).GetString(bytes);
    }

    private static byte[] DecodeQuotedPrintable(string value)
    {
        using var output = new MemoryStream();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '=' && index + 1 < value.Length &&
                (value[index + 1] == '\r' || value[index + 1] == '\n'))
            {
                index += value[index + 1] == '\r' && index + 2 < value.Length && value[index + 2] == '\n' ? 2 : 1;
            }
            else if (value[index] == '=' && index + 2 < value.Length &&
                byte.TryParse(value.AsSpan(index + 1, 2), System.Globalization.NumberStyles.HexNumber,
                    null, out var decoded))
            {
                output.WriteByte(decoded);
                index += 2;
            }
            else
            {
                output.WriteByte((byte)value[index]);
            }
        }

        return output.ToArray();
    }

    private static Encoding GetEncoding(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static ParameterizedValue ParseParameterized(string value)
    {
        var pieces = value.Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in pieces.Skip(1))
        {
            var equals = piece.IndexOf('=');
            if (equals > 0)
            {
                parameters[piece[..equals].Trim()] = piece[(equals + 1)..].Trim().Trim('"');
            }
        }

        return new ParameterizedValue(pieces[0].Trim(), parameters);
    }

    private static IEnumerable<string> SplitMultipart(string body, string boundary)
    {
        var marker = "--" + boundary;
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var segments = normalized.Split(marker, StringSplitOptions.None);
        foreach (var segment in segments.Skip(1))
        {
            if (segment.StartsWith("--", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return segment.TrimStart('\n').TrimEnd('\n');
        }
    }

    private static void SplitHeaderAndBody(string source, out string headers, out string body)
    {
        var separator = source.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var length = 4;
        if (separator < 0)
        {
            separator = source.IndexOf("\n\n", StringComparison.Ordinal);
            length = 2;
        }

        headers = separator < 0 ? source : source[..separator];
        body = separator < 0 ? string.Empty : source[(separator + length)..];
    }

    private sealed class ParseState(CancellationToken cancellationToken)
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal int PartCount { get; set; }
        internal bool Truncated { get; set; }
        internal string? PlainBody { get; set; }
        internal string? HtmlBody { get; set; }
        internal List<string> Attachments { get; } = [];
    }

    private sealed record MimePart(Dictionary<string, string> Headers, string Body);
    private sealed record ParameterizedValue(string Value, Dictionary<string, string> Parameters);
}
