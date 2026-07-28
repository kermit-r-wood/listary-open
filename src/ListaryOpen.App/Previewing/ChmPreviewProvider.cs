using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ListaryOpen.App.Previewing;

/// <summary>
/// Reads the uncompressed ITSF/ITSP directory of a compiled HTML Help file.
/// CHM topic data is commonly MSCompressed/LZX; this provider lists virtual
/// paths and section metadata but never invokes an LZX decoder or renders HTML.
/// </summary>
internal sealed class ChmPreviewProvider : IFilePreviewProvider
{
    private const long MaximumInputBytes = 8L * 1024 * 1024 * 1024;
    private const int ItsfV2HeaderLength = 0x58;
    private const int ItsfV3HeaderLength = 0x60;
    private const int ItspHeaderLength = 0x54;
    private const int MinimumBlockLength = 0x1000;
    private const int MaximumBlockLength = 64 * 1024;
    private const int MaximumDirectoryBytes = 32 * 1024 * 1024;
    private const int MaximumEntries = 500;
    private const int MaximumPathBytes = 32 * 1024;
    private const int MaximumOutputCharacters = 120_000;

    private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.ChmDocument);

    public Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.SizeBytes < ItsfV2HeaderLength || context.SizeBytes > MaximumInputBytes)
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
        var itsf = ReadRange(stream, 0, ItsfV3HeaderLength);
        if (!itsf.AsSpan(0, 4).SequenceEqual("ITSF"u8))
        {
            return null;
        }
        var version = BinaryPrimitives.ReadInt32LittleEndian(itsf.AsSpan(4));
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(itsf.AsSpan(8));
        if (version is not (2 or 3) || headerLength < (version == 2 ? ItsfV2HeaderLength : ItsfV3HeaderLength))
        {
            return null;
        }

        var directoryOffset = BinaryPrimitives.ReadUInt64LittleEndian(itsf.AsSpan(0x48));
        var directoryLength = BinaryPrimitives.ReadUInt64LittleEndian(itsf.AsSpan(0x50));
        var dataOffset = version == 3
            ? BinaryPrimitives.ReadUInt64LittleEndian(itsf.AsSpan(0x58))
            : checked(directoryOffset + directoryLength);
        if (!TryValidateRange(directoryOffset, directoryLength, stream.Length) ||
            directoryLength < ItspHeaderLength || directoryLength > MaximumDirectoryBytes ||
            dataOffset > (ulong)stream.Length)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = ReadRange(stream, checked((long)directoryOffset), checked((int)directoryLength));
        if (!directory.AsSpan(0, 4).SequenceEqual("ITSP"u8))
        {
            return null;
        }
        var itspVersion = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(4));
        var itspHeaderLength = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(8));
        var blockLength = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(0x10));
        var indexHead = BinaryPrimitives.ReadInt32LittleEndian(directory.AsSpan(0x20));
        var numberOfBlocks = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(0x28));
        if (itspVersion != 1 || itspHeaderLength != ItspHeaderLength ||
            blockLength is < MinimumBlockLength or > MaximumBlockLength ||
            numberOfBlocks == 0 || numberOfBlocks > 4096 ||
            (ulong)itspHeaderLength + ((ulong)numberOfBlocks * blockLength) > (ulong)directory.Length)
        {
            return null;
        }

        var entries = new List<ChmEntry>(Math.Min(MaximumEntries, checked((int)numberOfBlocks * 8)));
        var blockKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = false;
        for (var block = 0u; block < numberOfBlocks && entries.Count < MaximumEntries; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blockStart = checked(ItspHeaderLength + (int)(block * blockLength));
            var blockBytes = directory.AsSpan(blockStart, checked((int)blockLength));
            var marker = Encoding.ASCII.GetString(blockBytes[..4]);
            blockKinds[marker] = blockKinds.GetValueOrDefault(marker) + 1;
            if (!marker.Equals("PMGL", StringComparison.Ordinal))
            {
                continue;
            }
            var freeSpace = BinaryPrimitives.ReadUInt32LittleEndian(blockBytes[4..]);
            if (freeSpace > blockLength - 0x14)
            {
                truncated = true;
                continue;
            }
            var position = 0x14;
            var end = checked((int)blockLength - (int)freeSpace);
            while (position < end && entries.Count < MaximumEntries)
            {
                if (!TryReadCword(blockBytes, ref position, end, out var pathLength) ||
                    pathLength > MaximumPathBytes || pathLength > (ulong)(end - position))
                {
                    truncated = true;
                    break;
                }
                var pathBytes = blockBytes.Slice(position, checked((int)pathLength));
                position += checked((int)pathLength);
                string path;
                try
                {
                    path = Utf8Strict.GetString(pathBytes).Replace('\0', ' ').Trim();
                }
                catch (DecoderFallbackException)
                {
                    truncated = true;
                    break;
                }
                if (!TryReadCword(blockBytes, ref position, end, out var section) ||
                    !TryReadCword(blockBytes, ref position, end, out var offset) ||
                    !TryReadCword(blockBytes, ref position, end, out var length))
                {
                    truncated = true;
                    break;
                }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    entries.Add(new ChmEntry(path, section, offset, length));
                }
            }
        }

        var builder = new StringBuilder("CHM structure and directory")
            .AppendLine()
            .Append("ITSF version: ").AppendLine(version.ToString())
            .Append("Directory bytes: ").AppendLine(FormatSize(directory.Length))
            .Append("ITSP block size: ").AppendLine(blockLength.ToString())
            .Append("Directory blocks: ").AppendLine(numberOfBlocks.ToString())
            .Append("Index head block: ").AppendLine(indexHead.ToString());
        if (blockKinds.Count > 0)
        {
            builder.Append("Block types: ")
                .AppendLine(string.Join(", ", blockKinds.OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key} × {pair.Value}")));
        }
        builder.Append("Entries shown: ").AppendLine(entries.Count.ToString());
        if (entries.Count > 0)
        {
            builder.AppendLine().AppendLine("Virtual paths (payloads not opened):");
            foreach (var entry in entries)
            {
                builder.Append("- ").Append(entry.Path)
                    .Append(" [section ").Append(entry.Section)
                    .Append(", offset ").Append(entry.Offset)
                    .Append(", ").Append(entry.Length).AppendLine(" bytes]");
            }
        }
        if (truncated || entries.Count >= MaximumEntries)
        {
            builder.AppendLine().AppendLine("… CHM directory listing truncated at the preview limit");
        }
        builder.AppendLine()
            .AppendLine("MSCompressed/LZX topic payloads were not decompressed or rendered.");
        return PreviewContent.ForText("CHM structure and directory", Bound(builder.ToString()));
    }

    private static bool TryReadCword(ReadOnlySpan<byte> bytes, ref int position, int end, out ulong value)
    {
        value = 0;
        for (var count = 0; count < 10 && position < end; count++)
        {
            var current = bytes[position++];
            if (value > (ulong.MaxValue >> 7))
            {
                return false;
            }
            value = (value << 7) + (uint)(current & 0x7F);
            if (current < 0x80)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryValidateRange(ulong offset, ulong length, long streamLength) =>
        offset <= (ulong)streamLength && length <= (ulong)streamLength - offset;

    private static byte[] ReadRange(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
        {
            throw new InvalidDataException("CHM range is outside the file.");
        }
        stream.Position = offset;
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string Bound(string value) =>
        value.Length <= MaximumOutputCharacters
            ? value
            : value[..MaximumOutputCharacters] + Environment.NewLine + "…";

    private static string FormatSize(int size) => size >= 1024 * 1024
        ? $"{size / (1024d * 1024d):0.##} MiB"
        : size >= 1024 ? $"{size / 1024d:0.##} KiB" : $"{size} bytes";

    private readonly record struct ChmEntry(string Path, ulong Section, ulong Offset, ulong Length);
}
