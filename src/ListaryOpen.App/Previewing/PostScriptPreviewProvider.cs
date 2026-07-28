using System.Globalization;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads Document Structuring Convention comments and bounded literal strings
/// from PostScript-family files.  PostScript is a programming language, so the
/// preview worker never interprets, renders, or executes the program.
/// </summary>
internal sealed class PostScriptPreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 128L * 1024 * 1024;
    private const int MaximumScanBytes = 8 * 1024 * 1024;
    private const int MaximumOutputCharacters = 120_000;
    private const int MaximumLiteralCharacters = 24_000;
    private const int MaximumLiterals = 80;
    private static readonly byte[] BinaryEpsMagic = [0xC5, 0xD0, 0xD3, 0xC6];

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.PostScriptVector);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < 4 || context.SizeBytes > MaximumInputBytes)
        {
            return null;
        }

        using var stream = new FileStream(
            context.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        var scanLength = checked((int)Math.Min(stream.Length, MaximumScanBytes));
        var bytes = new byte[scanLength];
        stream.ReadExactly(bytes);
        cancellationToken.ThrowIfCancellationRequested();

        var postScript = SelectPostScriptPayload(bytes, stream.Length, out var binaryEps);
        if (postScript is null)
        {
            return null;
        }

        var text = Encoding.Latin1.GetString(postScript);
        var metadata = new DscMetadata();
        ParseDsc(text, metadata, cancellationToken);
        var literals = ExtractLiteralStrings(postScript, cancellationToken);
        var builder = new StringBuilder("PostScript structure and text")
            .AppendLine()
            .Append("Container: ").AppendLine(binaryEps ? "binary EPS wrapper" : "PostScript source")
            .Append("Scanned: ").AppendLine(FormatSize(postScript.Length));

        AppendIfPresent(builder, "Title", metadata.Title);
        AppendIfPresent(builder, "Creator", metadata.Creator);
        AppendIfPresent(builder, "Creation date", metadata.CreationDate);
        AppendIfPresent(builder, "Bounding box", metadata.BoundingBox);
        AppendIfPresent(builder, "Language level", metadata.LanguageLevel);
        if (metadata.Pages is not null)
        {
            builder.Append("Pages (DSC): ").AppendLine(metadata.Pages.Value.ToString(CultureInfo.InvariantCulture));
        }
        if (metadata.PageMarkers > 0)
        {
            builder.Append("Page markers: ").AppendLine(metadata.PageMarkers.ToString(CultureInfo.InvariantCulture));
        }
        if (metadata.DocumentFonts.Count > 0)
        {
            builder.Append("Document fonts: ").AppendLine(string.Join(", ", metadata.DocumentFonts));
        }
        if (metadata.CommentCount > 0)
        {
            builder.Append("DSC comments: ").AppendLine(metadata.CommentCount.ToString(CultureInfo.InvariantCulture));
        }
        if (literals.Count > 0)
        {
            builder.AppendLine().AppendLine("Literal text strings (not executed):");
            foreach (var literal in literals)
            {
                builder.Append("- ").AppendLine(literal);
            }
        }
        builder.AppendLine()
            .AppendLine("PostScript code was not interpreted or executed; external resources were not loaded.");
        return PreviewContent.ForText("PostScript structure and text", Bound(builder.ToString()));
    }

    private static byte[]? SelectPostScriptPayload(
        byte[] bytes,
        long streamLength,
        out bool binaryEps)
    {
        binaryEps = false;
        if (StartsWith(bytes, "%!PS"u8))
        {
            return bytes;
        }
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual(BinaryEpsMagic))
        {
            return null;
        }

        var offset = BitConverter.ToUInt32(bytes, 4);
        var length = BitConverter.ToUInt32(bytes, 8);
        if (offset > streamLength || length > streamLength - offset ||
            length == 0 || length > MaximumScanBytes ||
            offset > int.MaxValue || (ulong)offset + length > (ulong)bytes.Length)
        {
            return null;
        }
        var payload = bytes.AsSpan((int)offset, (int)length).ToArray();
        if (!StartsWith(payload, "%!PS"u8))
        {
            return null;
        }
        binaryEps = true;
        return payload;
    }

    private static bool StartsWith(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    private static void ParseDsc(string text, DscMetadata metadata, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = text.IndexOf('\n', offset);
            if (end < 0)
            {
                end = text.Length;
            }
            var lineLength = Math.Min(end - offset, 64 * 1024);
            var line = text.AsSpan(offset, lineLength).TrimEnd('\r');
            if (line.StartsWith("%%", StringComparison.Ordinal))
            {
                metadata.CommentCount++;
                if (line.StartsWith("%%Page:", StringComparison.Ordinal))
                {
                    metadata.PageMarkers++;
                }
                else
                {
                    var colon = line.IndexOf(':');
                    if (colon > 2)
                    {
                        var key = line[2..colon];
                        var value = line[(colon + 1)..].Trim();
                        var valueText = value.ToString();
                        switch (key)
                        {
                            case "Title": metadata.Title ??= CleanDsc(value); break;
                            case "Creator": metadata.Creator ??= CleanDsc(value); break;
                            case "CreationDate": metadata.CreationDate ??= CleanDsc(value); break;
                            case "BoundingBox": metadata.BoundingBox ??= ParseBoundingBox(value); break;
                            case "LanguageLevel": metadata.LanguageLevel ??= CleanDsc(value); break;
                            case "Pages":
                                if (int.TryParse(valueText.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages) &&
                                    pages >= 0 && pages <= 1_000_000)
                                {
                                    metadata.Pages = pages;
                                }
                                break;
                            case "DocumentFonts":
                                foreach (var font in valueText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                {
                                    if (metadata.DocumentFonts.Count >= 64) break;
                                    var cleaned = CleanDsc(font);
                                    if (!string.IsNullOrWhiteSpace(cleaned)) metadata.DocumentFonts.Add(cleaned);
                                }
                                break;
                        }
                    }
                }
            }
            offset = end < text.Length ? end + 1 : text.Length;
        }
    }

    private static string? ParseBoundingBox(ReadOnlySpan<char> value)
    {
        var tokens = value.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4 || tokens.Any(token => token.Equals("(atend)", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        var numbers = new double[4];
        for (var index = 0; index < numbers.Length; index++)
        {
            if (!double.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]))
            {
                return null;
            }
        }
        return string.Join(" ", numbers.Select(number => number.ToString("0.##", CultureInfo.InvariantCulture)));
    }

    private static string CleanDsc(ReadOnlySpan<char> value) =>
        value.ToString().Trim().Trim('(', ')').ReplaceLineEndings(" ");

    private static List<string> ExtractLiteralStrings(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        for (var index = 0; index < bytes.Length && results.Count < MaximumLiterals; index++)
        {
            if ((index & 0xFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (bytes[index] != (byte)'(' || (index > 0 && bytes[index - 1] == (byte)'\\')) continue;
            var depth = 1;
            var value = new StringBuilder();
            for (index++; index < bytes.Length && depth > 0; index++)
            {
                var current = bytes[index];
                if (current == (byte)'\\')
                {
                    if (++index >= bytes.Length) break;
                    AppendEscape(value, bytes, ref index);
                    continue;
                }
                if (current == (byte)'(')
                {
                    depth++;
                    if (depth <= 4) value.Append('(');
                    continue;
                }
                if (current == (byte)')')
                {
                    depth--;
                    if (depth > 0) value.Append(')');
                    continue;
                }
                if (value.Length < MaximumLiteralCharacters && current is >= 0x20 and <= 0xFF)
                {
                    value.Append((char)current);
                }
            }
            if (depth == 0)
            {
                var cleaned = value.ToString().ReplaceLineEndings(" ").Trim();
                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Any(char.IsLetterOrDigit))
                {
                    results.Add(cleaned.Length <= 300 ? cleaned : cleaned[..300] + "…");
                }
            }
        }
        return results;
    }

    private static void AppendEscape(StringBuilder value, ReadOnlySpan<byte> bytes, ref int index)
    {
        var escaped = bytes[index];
        if (escaped is (byte)'n' or (byte)'r' or (byte)'t' or (byte)'b' or (byte)'f')
        {
            value.Append(escaped switch { (byte)'n' => '\n', (byte)'r' => '\r', (byte)'t' => '\t', (byte)'b' => '\b', _ => '\f' });
            return;
        }
        if (escaped is (byte)'(' or (byte)')' or (byte)'\\')
        {
            value.Append((char)escaped);
            return;
        }
        if (escaped >= '0' && escaped <= '7')
        {
            var number = escaped - '0';
            for (var digit = 0; digit < 2 && index + 1 < bytes.Length; digit++)
            {
                var next = bytes[index + 1];
                if (next < '0' || next > '7') break;
                index++;
                number = (number * 8) + next - '0';
            }
            value.Append((char)number);
            return;
        }
        if (escaped != (byte)'\r' && escaped != (byte)'\n') value.Append((char)escaped);
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters ? value : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private static string FormatSize(int size) => size >= 1024 * 1024
        ? $"{size / (1024d * 1024d):0.##} MiB"
        : size >= 1024 ? $"{size / 1024d:0.##} KiB" : $"{size} bytes";

    private static void AppendIfPresent(StringBuilder builder, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) builder.Append(label).Append(": ").AppendLine(value);
    }

    private sealed class DscMetadata
    {
        internal string? Title { get; set; }
        internal string? Creator { get; set; }
        internal string? CreationDate { get; set; }
        internal string? BoundingBox { get; set; }
        internal string? LanguageLevel { get; set; }
        internal int? Pages { get; set; }
        internal int PageMarkers { get; set; }
        internal int CommentCount { get; set; }
        internal List<string> DocumentFonts { get; } = [];
    }
}
