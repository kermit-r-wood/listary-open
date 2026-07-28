using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads the bounded structural and text portions of a DjVu IFF file.  Page
/// forms, INFO dimensions, and TXTa text are safe to inspect directly. TXTz,
/// image, JB2, and annotation payloads remain opaque because they use DjVu's
/// BZZ/image codecs and are never decompressed by the preview worker.
/// </summary>
internal sealed class DjvuPreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumChunks = 2000;
    private const int MaximumPages = 200;
    private const int MaximumTextChunkBytes = 4 * 1024 * 1024;
    private const int MaximumTextBytes = 1024 * 1024;
    private const int MaximumOutputCharacters = 120_000;
    private const int MaximumDepth = 4;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.DjvuDocument);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < 12 || context.SizeBytes > MaximumInputBytes)
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
        Span<byte> magic = stackalloc byte[4];
        if (!TryReadExactly(stream, magic) || !magic.SequenceEqual("AT&T"u8))
        {
            return null;
        }

        var state = new ScanState(cancellationToken);
        if (!ScanChunk(stream, stream.Length, 0, null, state) || !state.SawForm)
        {
            return null;
        }

        var builder = new StringBuilder("DjVu structure and text")
            .AppendLine()
            .Append("Document form: ").AppendLine(state.DocumentForm ?? "unknown")
            .Append("Pages: ").AppendLine(state.PageCount.ToString("N0"))
            .Append("Chunks scanned: ").AppendLine(state.ChunkCount.ToString("N0"));
        if (state.CompressedTextChunks > 0)
        {
            builder.Append("Compressed text chunks (TXTz/BZZ, not decompressed): ")
                .AppendLine(state.CompressedTextChunks.ToString("N0"));
        }
        if (state.PageDimensions.Count > 0)
        {
            builder.AppendLine().AppendLine("Page dimensions:");
            foreach (var dimension in state.PageDimensions)
            {
                builder.Append("- ").AppendLine(dimension);
            }
        }
        if (state.TextBuilder.Length > 0)
        {
            builder.AppendLine().AppendLine("Uncompressed OCR text (TXTa):")
                .AppendLine(state.TextBuilder.ToString());
        }
        if (state.TextTruncated || state.ChunkCount >= MaximumChunks || state.PageCount >= MaximumPages)
        {
            builder.AppendLine().AppendLine("… DjVu preview scan truncated at the preview limit");
        }
        builder.AppendLine()
            .AppendLine("Image, JB2, annotation, and BZZ-compressed payloads were not decoded.");
        return PreviewContent.ForText("DjVu structure and text", Bound(builder.ToString()));
    }

    private static bool ScanChunk(
        FileStream stream,
        long end,
        int depth,
        string? parentForm,
        ScanState state)
    {
        if (depth > MaximumDepth)
        {
            return false;
        }
        while (stream.Position + 8 <= end)
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (state.ChunkCount >= MaximumChunks)
            {
                return true;
            }

            var header = new byte[8];
            if (!TryReadExactly(stream, header))
            {
                return false;
            }
            var id = Encoding.ASCII.GetString(header[..4]);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var payloadStart = stream.Position;
            var payloadEnd = checked(payloadStart + length);
            var paddedEnd = checked(payloadEnd + (length & 1));
            if (payloadEnd > end || paddedEnd > stream.Length)
            {
                return false;
            }
            state.ChunkCount++;

            if (id.Equals("FORM", StringComparison.Ordinal))
            {
                if (length < 4)
                {
                    return false;
                }
                var formTypeBytes = new byte[4];
                if (!TryReadExactly(stream, formTypeBytes))
                {
                    return false;
                }
                var formType = Encoding.ASCII.GetString(formTypeBytes);
                state.SawForm = true;
                state.DocumentForm ??= formType;
                if (formType.Equals("DJVU", StringComparison.Ordinal))
                {
                    if (state.PageCount < MaximumPages)
                    {
                        state.PageCount++;
                    }
                }
                if (!ScanChunk(stream, payloadEnd, depth + 1, formType, state))
                {
                    return false;
                }
            }
            else if (id.Equals("TXTa", StringComparison.Ordinal))
            {
                ReadPlainTextChunk(stream, payloadStart, length, state);
            }
            else if (id.Equals("TXTz", StringComparison.Ordinal))
            {
                state.CompressedTextChunks++;
            }
            else if (id.Equals("INFO", StringComparison.Ordinal) &&
                     string.Equals(parentForm, "DJVU", StringComparison.Ordinal))
            {
                ReadInfoChunk(stream, payloadStart, length, state);
            }

            stream.Position = paddedEnd;
        }
        return stream.Position == end || stream.Position + 1 == end;
    }

    private static void ReadPlainTextChunk(
        FileStream stream,
        long payloadStart,
        uint length,
        ScanState state)
    {
        if (length < 4 || length > MaximumTextChunkBytes)
        {
            state.TextTruncated = true;
            return;
        }
        var bytes = new byte[checked((int)length)];
        stream.Position = payloadStart;
        if (!TryReadExactly(stream, bytes))
        {
            state.TextTruncated = true;
            return;
        }
        var textLength = ReadBigEndian24(bytes);
        if (textLength > bytes.Length - 4)
        {
            state.TextTruncated = true;
            return;
        }
        var available = (int)Math.Min(
            textLength,
            (uint)Math.Max(0, MaximumTextBytes - state.TextBytes));
        if (available > 0)
        {
            var text = Encoding.UTF8.GetString(bytes, 3, available);
            AppendBounded(state.TextBuilder, text, MaximumOutputCharacters);
            state.TextBytes += available;
        }
        if (textLength > available)
        {
            state.TextTruncated = true;
        }
    }

    private static void ReadInfoChunk(
        FileStream stream,
        long payloadStart,
        uint length,
        ScanState state)
    {
        if (length < 4 || state.PageDimensions.Count >= MaximumPages)
        {
            return;
        }
        Span<byte> info = stackalloc byte[10];
        stream.Position = payloadStart;
        var count = (int)Math.Min(length, (uint)info.Length);
        if (!TryReadExactly(stream, info[..count]) || count < 4)
        {
            return;
        }
        var width = BinaryPrimitives.ReadUInt16BigEndian(info[..2]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(info[2..4]);
        state.PageDimensions.Add($"{width:N0} × {height:N0}");
    }

    private static uint ReadBigEndian24(ReadOnlySpan<byte> bytes) =>
        (uint)((bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = stream.Read(destination[read..]);
            if (count == 0)
            {
                return false;
            }
            read += count;
        }
        return true;
    }

    private static void AppendBounded(StringBuilder builder, string value, int maximum)
    {
        if (builder.Length >= maximum)
        {
            return;
        }
        var remaining = maximum - builder.Length;
        builder.Append(value.Length <= remaining ? value : value[..remaining]);
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private sealed class ScanState(CancellationToken cancellationToken)
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal StringBuilder TextBuilder { get; } = new();
        internal List<string> PageDimensions { get; } = [];
        internal string? DocumentForm { get; set; }
        internal int ChunkCount { get; set; }
        internal int PageCount { get; set; }
        internal int CompressedTextChunks { get; set; }
        internal int TextBytes { get; set; }
        internal bool TextTruncated { get; set; }
        internal bool SawForm { get; set; }
    }
}
