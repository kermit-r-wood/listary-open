using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads only LHA/LZH entry headers.  No compressed bytes are decompressed or
/// copied into memory, so a preview cannot execute an archive's content or
/// trigger a decompression bomb. Level 0 and level 1 headers are supported;
/// unknown/unsupported header variants safely fall back to the opaque reader.
/// </summary>
internal sealed class LhaArchivePreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 8L * 1024 * 1024 * 1024;
    private const long MaximumHeaderScanBytes = 8L * 1024 * 1024;
    private const int MaximumEntries = 200;
    private const int MaximumOutputCharacters = 120_000;
    private const int MaximumHeaderBytes = 64 * 1024;
    private const int MaximumExtensionBytes = 1 * 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.LhaArchive);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < 24 || context.SizeBytes > MaximumInputBytes)
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
        var builder = new StringBuilder("LHA/LZH archive directory")
            .AppendLine()
            .AppendLine()
            .AppendLine("Entry headers only; compressed payloads were not opened or decompressed:");
        var entryCount = 0;
        var headerBytes = 0L;
        var packedTotal = 0L;
        var originalTotal = 0L;
        var sawEntry = false;
        var truncated = false;

        while (stream.Position < stream.Length && entryCount < MaximumEntries &&
               builder.Length < MaximumOutputCharacters && headerBytes < MaximumHeaderScanBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPosition = stream.Position;
            var parsed = TryReadEntry(stream, cancellationToken);
            if (parsed is null)
            {
                break;
            }

            sawEntry = true;
            entryCount++;
            headerBytes = checked(headerBytes + parsed.HeaderAndExtensionBytes);
            packedTotal = checked(packedTotal + parsed.PackedSize);
            originalTotal = checked(originalTotal + parsed.OriginalSize);
            builder.Append("- ").Append(Truncate(parsed.Path, 512))
                .Append(" [").Append(parsed.Method).Append("] ")
                .Append(parsed.OriginalSize.ToString("N0"))
                .Append(" bytes");
            if (parsed.IsDirectory)
            {
                builder.Append(" (directory)");
            }
            else if (parsed.PackedSize != parsed.OriginalSize)
            {
                builder.Append(" packed ").Append(parsed.PackedSize.ToString("N0"));
            }
            builder.AppendLine();

            var nextPosition = checked(entryPosition + parsed.TotalHeaderBytes + parsed.PackedSize);
            if (nextPosition <= stream.Position || nextPosition > stream.Length)
            {
                break;
            }
            stream.Position = nextPosition;
        }

        if (!sawEntry)
        {
            return null;
        }

        if (entryCount >= MaximumEntries || builder.Length >= MaximumOutputCharacters ||
            headerBytes >= MaximumHeaderScanBytes)
        {
            truncated = true;
        }
        if (truncated)
        {
            builder.AppendLine("… entry listing truncated at the preview limit");
        }
        builder.AppendLine()
            .Append("Entries shown: ").AppendLine(entryCount.ToString("N0"))
            .Append("Packed bytes declared: ").AppendLine(packedTotal.ToString("N0"))
            .Append("Original bytes declared: ").Append(originalTotal.ToString("N0"));
        return PreviewContent.ForText("LHA archive directory", Bound(builder.ToString()));
    }

    private static LhaEntry? TryReadEntry(FileStream stream, CancellationToken cancellationToken)
    {
        var position = stream.Position;
        Span<byte> prefix = stackalloc byte[21];
        if (!TryReadExactly(stream, prefix))
        {
            return null;
        }

        var method = Encoding.ASCII.GetString(prefix[2..7]);
        if (!method.StartsWith("-l", StringComparison.Ordinal))
        {
            return null;
        }

        var packedSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[7..11]);
        var originalSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix[11..15]);
        var level = prefix[20];
        if (level > 2)
        {
            return null;
        }

        var totalHeaderBytes = level == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(prefix[..2])
            : prefix[0] + 2L;
        if (totalHeaderBytes < (level == 2 ? 24 : 22) ||
            totalHeaderBytes > MaximumHeaderBytes ||
            totalHeaderBytes > stream.Length - position)
        {
            return null;
        }

        var remainingHeaderBytes = checked((int)totalHeaderBytes - prefix.Length);
        var rest = new byte[remainingHeaderBytes];
        if (!TryReadExactly(stream, rest))
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();

        var path = string.Empty;
        var directory = string.Empty;
        var extensionBytes = 0L;
        if (level is 0 or 1)
        {
            var pathLength = rest[0];
            if (pathLength > rest.Length - 1)
            {
                return null;
            }
            path = DecodeName(rest.AsSpan(1, pathLength));
            var offset = 1 + pathLength;
            if (level == 1)
            {
                if (offset + 3 > rest.Length)
                {
                    return null;
                }
                // Data CRC and creator/OS byte are part of the base header.
                // The level-1 header's trailing two bytes are the first
                // extension-size field; extension payloads follow it.
                stream.Position = checked(position + totalHeaderBytes - 2);
                if (!TryReadLevel1Extensions(
                        stream,
                        packedSize,
                        cancellationToken,
                        out extensionBytes,
                        out var extensionName,
                        out directory))
                {
                    return null;
                }
                if (!string.IsNullOrWhiteSpace(extensionName))
                {
                    path = extensionName;
                }
            }
        }
        else
        {
            // Level 2 stores a CRC, creator/OS byte, and extended headers in
            // the two-byte-sized header itself. The compressed-size field does
            // not include these extension headers.
            if (rest.Length < 3)
            {
                return null;
            }
            if (!TryReadLevel2Extensions(
                    rest,
                    cancellationToken,
                    out extensionBytes,
                    out path,
                    out directory))
            {
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(directory))
        {
            path = directory.TrimEnd('/') + "/" + path;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            path = method.Equals("-lhd-", StringComparison.Ordinal)
                ? directory.TrimEnd('/')
                : "(unnamed entry)";
        }
        if (string.IsNullOrWhiteSpace(path) ||
            (level == 1 && extensionBytes < 2) ||
            (level == 1 && extensionBytes - 2 > packedSize) ||
            position + totalHeaderBytes + packedSize > stream.Length)
        {
            return null;
        }
        return new LhaEntry(
            path,
            method,
            packedSize,
            originalSize,
            checked(totalHeaderBytes),
            checked(totalHeaderBytes + Math.Max(0, extensionBytes - 2)),
            method.Equals("-lhd-", StringComparison.Ordinal));
    }

    private static bool TryReadLevel1Extensions(
        FileStream stream,
        uint packedSize,
        CancellationToken cancellationToken,
        out long extensionBytes,
        out string filename,
        out string directory)
    {
        extensionBytes = 0;
        filename = string.Empty;
        directory = string.Empty;
        Span<byte> sizeBytes = stackalloc byte[2];
        if (!TryReadExactly(stream, sizeBytes))
        {
            return false;
        }
        extensionBytes = 2;
        var extensionSize = BinaryPrimitives.ReadUInt16LittleEndian(sizeBytes);
        while (extensionSize != 0)
        {
            if (extensionSize < 3 || extensionBytes > MaximumExtensionBytes ||
                extensionBytes + extensionSize > packedSize)
            {
                return false;
            }
            // Level-1's size value covers type, data, and the following
            // two-byte next-header-size field; the value itself was read
            // separately above.
            var body = new byte[extensionSize];
            if (!TryReadExactly(stream, body))
            {
                return false;
            }
            extensionBytes = checked(extensionBytes + body.Length);
            var dataLength = extensionSize - 3;
            ParseExtension(body[0], body.AsSpan(1, dataLength), ref filename, ref directory);
            extensionSize = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(body.Length - 2));
            cancellationToken.ThrowIfCancellationRequested();
        }
        return true;
    }

    private static bool TryReadLevel2Extensions(
        ReadOnlySpan<byte> header,
        CancellationToken cancellationToken,
        out long extensionBytes,
        out string filename,
        out string directory)
    {
        extensionBytes = 0;
        filename = string.Empty;
        directory = string.Empty;
        var offset = 3; // CRC16 and creator/OS byte.
        while (offset + 2 <= header.Length)
        {
            var extensionSize = BinaryPrimitives.ReadUInt16LittleEndian(header[offset..]);
            offset += 2;
            extensionBytes = checked(extensionBytes + 2);
            if (extensionSize == 0)
            {
                return true;
            }
            if (extensionSize < 3 || offset + extensionSize - 2 > header.Length ||
                extensionBytes > MaximumExtensionBytes - extensionSize)
            {
                return false;
            }
            var body = header.Slice(offset, extensionSize - 2);
            extensionBytes = checked(extensionBytes + body.Length);
            var dataLength = extensionSize - 3;
            ParseExtension(body[0], body.Slice(1, dataLength), ref filename, ref directory);
            offset += body.Length;
            cancellationToken.ThrowIfCancellationRequested();
        }
        return false;
    }

    private static void ParseExtension(
        byte extensionType,
        ReadOnlySpan<byte> data,
        ref string filename,
        ref string directory)
    {
        switch (extensionType)
        {
            case 1:
                filename = DecodeName(data);
                break;
            case 2:
                directory = DecodeName(data).Replace('\0', '/');
                break;
            case 0x44:
                filename = DecodeUtf16Name(data);
                break;
            case 0x45:
                directory = DecodeUtf16Name(data).Replace('\0', '/');
                break;
        }
    }

    private static string DecodeUtf16Name(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 2
            ? Encoding.Unicode.GetString(bytes).Trim('\0', ' ')
            : string.Empty;
    }

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

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }
        var utf8 = Encoding.UTF8.GetString(bytes).Trim('\0', ' ');
        return utf8.Contains('\uFFFD', StringComparison.Ordinal)
            ? Encoding.Latin1.GetString(bytes).Trim('\0', ' ')
            : utf8;
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum] + "…";

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private sealed record LhaEntry(
        string Path,
        string Method,
        uint PackedSize,
        uint OriginalSize,
        long TotalHeaderBytes,
        long HeaderAndExtensionBytes,
        bool IsDirectory);
}
