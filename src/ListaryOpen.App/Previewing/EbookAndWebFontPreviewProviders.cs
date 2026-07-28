using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ListaryOpen.App.Previewing;

internal sealed class WebFontPreviewProvider : IFilePreviewProvider
{
    private const int WoffHeaderLength = 44;
    private const int Woff2HeaderLength = 48;
    private const int MaximumNameTableBytes = 1024 * 1024;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.WebFont);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        if (context.Extension.Equals(".eot", StringComparison.OrdinalIgnoreCase))
        {
            return LoadEot(context, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var required = context.Extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
            ? Woff2HeaderLength : WoffHeaderLength;
        if (stream.Length < required)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[Woff2HeaderLength];
        stream.ReadExactly(header[..required]);
        var signature = BinaryPrimitives.ReadUInt32BigEndian(header);
        var expected = context.Extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase)
            ? 0x774F4632u : 0x774F4646u;
        if (signature != expected)
        {
            return null;
        }

        var isWoff2 = context.Extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase);
        var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var tables = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);
        var reserved = BinaryPrimitives.ReadUInt16BigEndian(header[14..]);
        var sfntSize = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        if (declaredLength != stream.Length || tables is 0 or > 256 || reserved != 0 ||
            sfntSize == 0 || sfntSize > 64 * 1024 * 1024)
        {
            return null;
        }
        WoffTable? nameTable = null;
        if (isWoff2)
        {
            if (!ValidateWoff2(header, declaredLength))
            {
                return null;
            }
        }
        else if (!ValidateWoff(stream, header, declaredLength, tables, out nameTable))
        {
            return null;
        }

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
        var versionOffset = isWoff2 ? 24 : 20;
        var major = BinaryPrimitives.ReadUInt16BigEndian(header[versionOffset..]);
        var minor = BinaryPrimitives.ReadUInt16BigEndian(header[(versionOffset + 2)..]);
        var builder = new StringBuilder(isWoff2
                ? "Web Open Font Format 2" : "Web Open Font Format")
            .AppendLine().AppendLine()
            .Append("SFNT flavor: ").AppendLine(FormatFlavor(flavor))
            .Append("Tables: ").AppendLine(tables.ToString())
            .Append("Container size: ").AppendLine(FormatSize(declaredLength))
            .Append("Original SFNT size: ").AppendLine(FormatSize(sfntSize))
            .Append("Version: ").Append(major).Append('.').AppendLine(minor.ToString());
        if (required == Woff2HeaderLength)
        {
            builder.Append("Compressed font data: ")
                .AppendLine(FormatSize(BinaryPrimitives.ReadUInt32BigEndian(header[20..])));
        }
        else
        {
            AppendWoffIdentity(builder, stream, nameTable, cancellationToken);
        }

        return PreviewContent.ForText("Web font metadata", builder.ToString());
    }

    private static PreviewContent? LoadEot(PreviewContext context, CancellationToken cancellationToken)
    {
        const int FixedHeaderBytes = 34;
        const long MaximumInputBytes = 64L * 1024 * 1024;
        const int MaximumNameBytes = 32 * 1024;
        if (context.SizeBytes < FixedHeaderBytes || context.SizeBytes > MaximumInputBytes)
        {
            return null;
        }

        using var stream = new FileStream(
            context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        Span<byte> fixedHeader = stackalloc byte[FixedHeaderBytes];
        stream.ReadExactly(fixedHeader);
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader);
        var fontDataSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[4..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[12..]);
        if (version is not (0x00010000u or 0x00020001u or 0x00020002u) ||
            declaredSize < FixedHeaderBytes || declaredSize > stream.Length ||
            fontDataSize > declaredSize - FixedHeaderBytes)
        {
            return null;
        }

        var styleLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedHeader[32..]);
        var style = ReadEotStringBody(stream, styleLength, MaximumNameBytes);
        var versionName = ReadEotString(stream, MaximumNameBytes);
        var fullName = ReadEotString(stream, MaximumNameBytes);
        if (style is null || versionName is null || fullName is null ||
            (ulong)stream.Position + fontDataSize > declaredSize)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var builder = new StringBuilder("Embedded OpenType font metadata")
            .AppendLine().AppendLine()
            .Append("EOT version: ").AppendLine($"0x{version:X8}")
            .Append("Declared size: ").AppendLine(FormatSize(declaredSize))
            .Append("Font data: ").AppendLine(FormatSize(fontDataSize))
            .Append("Flags: ").AppendLine($"0x{flags:X8}")
            .Append("Charset: ").AppendLine(fixedHeader[26].ToString())
            .Append("Italic: ").AppendLine(fixedHeader[27] == 0 ? "no" : "yes")
            .Append("Weight: ").AppendLine(BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader[28..]).ToString())
            .Append("Style: ").AppendLine(style)
            .Append("Version name: ").AppendLine(versionName)
            .Append("Full name: ").AppendLine(fullName)
            .AppendLine()
            .AppendLine("Embedded font bytes were not decompressed, decrypted, installed, or rendered.");
        return PreviewContent.ForText("Embedded OpenType metadata", builder.ToString());
    }

    private static string? ReadEotString(FileStream stream, int maximumBytes)
    {
        Span<byte> lengthBytes = stackalloc byte[2];
        stream.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        return ReadEotStringBody(stream, length, maximumBytes);
    }

    private static string? ReadEotStringBody(FileStream stream, int length, int maximumBytes)
    {
        if (length > maximumBytes || (length & 1) != 0 || length > stream.Length - stream.Position)
        {
            return null;
        }
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        Span<byte> padding = stackalloc byte[2];
        stream.ReadExactly(padding);
        if (BinaryPrimitives.ReadUInt16LittleEndian(padding) != 0)
        {
            return null;
        }
        return Encoding.Unicode.GetString(bytes).Trim('\0', ' ', '\r', '\n');
    }

    private static bool ValidateWoff(
        Stream stream,
        ReadOnlySpan<byte> header,
        uint declaredLength,
        ushort tables,
        out WoffTable? nameTable)
    {
        nameTable = null;
        var directoryLength = checked(tables * 20);
        if (WoffHeaderLength + directoryLength > declaredLength)
        {
            return false;
        }
        var directory = new byte[directoryLength];
        stream.ReadExactly(directory);
        var ranges = new List<(uint Start, uint End)>(tables + 2);
        var tags = new HashSet<uint>();
        var dataStart = checked((uint)(WoffHeaderLength + directoryLength));
        ulong expectedSfntSize = 12UL + (16UL * tables);
        for (var index = 0; index < tables; index++)
        {
            var entry = directory.AsSpan(index * 20, 20);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var compressedLength = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var originalLength = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
            var paddedCompressedLength = Align4(compressedLength);
            if (!tags.Add(tag) || offset % 4 != 0 || compressedLength == 0 || originalLength == 0 ||
                compressedLength > originalLength || originalLength > 16 * 1024 * 1024 ||
                !TryAddRange(ranges, offset, paddedCompressedLength, declaredLength, dataStart))
            {
                return false;
            }
            expectedSfntSize += Align4(originalLength);
            if (expectedSfntSize > 64 * 1024 * 1024)
            {
                return false;
            }
            if (tag == 0x6E616D65)
            {
                nameTable = new WoffTable(offset, compressedLength, originalLength, checksum);
            }
        }
        if (expectedSfntSize != BinaryPrimitives.ReadUInt32BigEndian(header[16..]))
        {
            return false;
        }
        var metadataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[24..]);
        var metadataLength = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        var metadataOriginalLength = BinaryPrimitives.ReadUInt32BigEndian(header[32..]);
        var privateOffset = BinaryPrimitives.ReadUInt32BigEndian(header[36..]);
        var privateLength = BinaryPrimitives.ReadUInt32BigEndian(header[40..]);
        return ValidateOptionalRange(ranges, metadataOffset, metadataLength, declaredLength, dataStart) &&
            (metadataLength == 0 ? metadataOriginalLength == 0 : metadataOriginalLength >= metadataLength) &&
            ValidateOptionalRange(ranges, privateOffset, privateLength, declaredLength, dataStart);
    }

    private static void AppendWoffIdentity(
        StringBuilder? builder,
        Stream stream,
        WoffTable? table,
        CancellationToken cancellationToken)
    {
        if (builder is null || table is null ||
            table.Value.OriginalLength > MaximumNameTableBytes)
        {
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = table.Value.Offset;
        var compressed = new byte[checked((int)table.Value.CompressedLength)];
        stream.ReadExactly(compressed);
        byte[] bytes;
        if (table.Value.CompressedLength == table.Value.OriginalLength)
        {
            bytes = compressed;
        }
        else
        {
            bytes = new byte[checked((int)table.Value.OriginalLength)];
            using var source = new MemoryStream(compressed, writable: false);
            using var inflater = new ZLibStream(source, CompressionMode.Decompress);
            inflater.ReadExactly(bytes);
            if (inflater.ReadByte() != -1)
            {
                throw new InvalidDataException("WOFF name table expands beyond its declared size.");
            }
        }
        if (CalculateSfntChecksum(bytes) != table.Value.Checksum)
        {
            throw new InvalidDataException("WOFF name-table checksum is invalid.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (label, value) in ReadNames(bytes))
        {
            builder.Append(label).Append(": ").AppendLine(value);
        }
    }

    private static IReadOnlyList<(string Label, string Value)> ReadNames(ReadOnlySpan<byte> bytes)
    {
        var results = new List<(string Label, string Value)>();
        if (bytes.Length < 6)
        {
            return results;
        }
        var format = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        var stringOffset = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        var recordsEnd = 6L + count * 12L;
        if (format is not (0 or 1) || count > 256 ||
            recordsEnd > bytes.Length || stringOffset > bytes.Length)
        {
            return results;
        }
        if (format == 1)
        {
            if (recordsEnd + 2 > bytes.Length)
            {
                return results;
            }
            var languageTags = BinaryPrimitives.ReadUInt16BigEndian(bytes[(int)recordsEnd..]);
            recordsEnd += 2L + languageTags * 4L;
        }
        if (recordsEnd > bytes.Length || stringOffset < recordsEnd)
        {
            return results;
        }
        var selected = new Dictionary<ushort, (int Priority, string Value)>();
        for (var index = 0; index < count; index++)
        {
            var record = bytes.Slice(6 + index * 12, 12);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (length > 4096 || (platform is 0 or 3 && length % 2 != 0) ||
                (long)stringOffset + offset + length > bytes.Length ||
                !NameLabels.ContainsKey(nameId))
            {
                continue;
            }
            var valueBytes = bytes.Slice(stringOffset + offset, length);
            var value = platform is 0 or 3
                ? DecodeBigEndianUnicode(valueBytes)
                : Encoding.Latin1.GetString(valueBytes);
            value = value.Replace('\0', ' ').ReplaceLineEndings(" ").Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            var priority = platform == 3 ? 3 : platform == 0 ? 2 : 1;
            if (!selected.TryGetValue(nameId, out var current) || priority > current.Priority)
            {
                selected[nameId] = (priority, value);
            }
        }
        foreach (var nameId in new ushort[] { 1, 2, 4, 6, 5, 8, 13 })
        {
            if (selected.TryGetValue(nameId, out var item))
            {
                results.Add((NameLabels[nameId], item.Value));
            }
        }
        return results;
    }

    private static string DecodeBigEndianUnicode(ReadOnlySpan<byte> bytes) =>
        Encoding.BigEndianUnicode.GetString(bytes);

    private static readonly IReadOnlyDictionary<ushort, string> NameLabels =
        new Dictionary<ushort, string>
        {
            [1] = "Family",
            [2] = "Subfamily",
            [4] = "Full name",
            [5] = "Font version",
            [6] = "PostScript name",
            [8] = "Manufacturer",
            [13] = "License"
        };

    private readonly record struct WoffTable(
        uint Offset,
        uint CompressedLength,
        uint OriginalLength,
        uint Checksum);

    private static bool ValidateWoff2(ReadOnlySpan<byte> header, uint declaredLength)
    {
        var totalCompressedSize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
        var metadataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[28..]);
        var metadataLength = BinaryPrimitives.ReadUInt32BigEndian(header[32..]);
        var metadataOriginalLength = BinaryPrimitives.ReadUInt32BigEndian(header[36..]);
        var privateOffset = BinaryPrimitives.ReadUInt32BigEndian(header[40..]);
        var privateLength = BinaryPrimitives.ReadUInt32BigEndian(header[44..]);
        var ranges = new List<(uint Start, uint End)>(2);
        return totalCompressedSize > 0 &&
            totalCompressedSize <= declaredLength - Woff2HeaderLength &&
            ValidateOptionalRange(ranges, metadataOffset, metadataLength, declaredLength, Woff2HeaderLength) &&
            (metadataLength == 0 ? metadataOriginalLength == 0 : metadataOriginalLength >= metadataLength) &&
            ValidateOptionalRange(ranges, privateOffset, privateLength, declaredLength, Woff2HeaderLength);
    }

    private static bool ValidateOptionalRange(
        List<(uint Start, uint End)> ranges,
        uint offset,
        uint length,
        uint declaredLength,
        uint minimumOffset) =>
        offset == 0 && length == 0 ||
        offset != 0 && length != 0 &&
        TryAddRange(ranges, offset, length, declaredLength, minimumOffset);

    private static bool TryAddRange(
        List<(uint Start, uint End)> ranges,
        uint offset,
        uint length,
        uint declaredLength,
        uint minimumOffset)
    {
        var end = (ulong)offset + length;
        if (offset < minimumOffset || end > declaredLength)
        {
            return false;
        }
        foreach (var range in ranges)
        {
            if (offset < range.End && end > range.Start)
            {
                return false;
            }
        }
        ranges.Add((offset, (uint)end));
        return true;
    }

    private static uint Align4(uint value) => checked((value + 3u) & ~3u);

    private static uint CalculateSfntChecksum(ReadOnlySpan<byte> bytes)
    {
        uint checksum = 0;
        Span<byte> word = stackalloc byte[4];
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            word.Clear();
            bytes.Slice(offset, Math.Min(4, bytes.Length - offset)).CopyTo(word);
            checksum = unchecked(checksum + BinaryPrimitives.ReadUInt32BigEndian(word));
        }
        return checksum;
    }

    private static string FormatFlavor(uint value) => value switch
    {
        0x00010000 => "TrueType",
        0x4F54544F => "OpenType/CFF",
        0x74727565 => "TrueType (Apple)",
        _ => $"0x{value:X8}"
    };

    private static string FormatSize(uint size) => size >= 1024 * 1024
        ? $"{size / (1024d * 1024d):0.##} MiB"
        : size >= 1024 ? $"{size / 1024d:0.##} KiB" : $"{size} bytes";
}

internal sealed class EbookPreviewProvider : IFilePreviewProvider
{
    private const int MaximumRecordInputBytes = 2 * 1024 * 1024;
    private const int MaximumTextBytes = 512 * 1024;
    private const int MaximumRecords = 4096;

    static EbookPreviewProvider() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Ebook);

    public Task<PreviewContent?> LoadAsync(PreviewContext context, CancellationToken cancellationToken) =>
        Task.Run<PreviewContent?>(() => Load(context, cancellationToken), cancellationToken);

    private static PreviewContent? Load(PreviewContext context, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(context.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < 86)
        {
            return null;
        }

        var pdbHeader = ReadRange(stream, 0, 78);
        var recordCount = ReadU16(pdbHeader, 76);
        if (recordCount == 0 || recordCount > MaximumRecords ||
            78L + (recordCount * 8L) > stream.Length)
        {
            return null;
        }

        var type = Encoding.ASCII.GetString(pdbHeader.AsSpan(60, 4));
        var creator = Encoding.ASCII.GetString(pdbHeader.AsSpan(64, 4));
        var recognizedPdb = (type == "BOOK" && creator == "MOBI") ||
            (type == "TEXt" && creator == "REAd");
        if (!recognizedPdb)
        {
            return null;
        }

        var directory = ReadRange(stream, 78, checked(recordCount * 8));
        var offsets = new long[recordCount + 1];
        for (var index = 0; index < recordCount; index++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(directory.AsSpan(index * 8, 4));
            if (offset > stream.Length || (index > 0 && offset < offsets[index - 1]))
            {
                return null;
            }
            offsets[index] = offset;
        }
        offsets[recordCount] = stream.Length;
        if (offsets[0] < 78L + (recordCount * 8L) || offsets[0] + 16 > offsets[1])
        {
            return null;
        }

        var record0Length = offsets[1] - offsets[0];
        if (record0Length > MaximumRecordInputBytes)
        {
            return null;
        }
        var record0 = ReadRange(stream, offsets[0], checked((int)record0Length)).AsSpan();
        var compression = ReadU16(record0, 0);
        var textLength = ReadU32Value(record0, 4);
        var textRecords = ReadU16(record0, 8);
        var encryption = ReadU16(record0, 12);
        var hasMobi = record0.Length >= 20 &&
            record0.Slice(16, 4).SequenceEqual("MOBI"u8);
        var encoding = Encoding.UTF8;
        var title = DecodePdbName(pdbHeader.AsSpan(0, 32));
        var mobiVersion = 0;
        if (hasMobi)
        {
            var mobiLength = ReadU32(record0, 20);
            if (mobiLength < 116 || 16L + mobiLength > record0.Length)
            {
                return null;
            }
            encoding = ReadU32(record0, 28) == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(1252);
            mobiVersion = ReadU32(record0, 36);
            var titleOffset = ReadU32(record0, 100);
            var titleLength = ReadU32(record0, 104);
            if (titleLength > 0 && titleOffset <= record0.Length &&
                titleLength <= record0.Length - titleOffset)
            {
                title = encoding.GetString(record0.Slice(titleOffset, titleLength)).Trim('\0', ' ');
            }
        }

        var builder = new StringBuilder("Mobipocket / PalmDOC ebook").AppendLine().AppendLine()
            .Append("Title: ").AppendLine(title)
            .Append("PDB type/creator: ").Append(type).Append('/').AppendLine(creator)
            .Append("Records: ").AppendLine(recordCount.ToString())
            .Append("Text length: ").AppendLine(textLength.ToString())
            .Append("Compression: ").AppendLine(compression switch
            {
                1 => "None",
                2 => "PalmDOC LZ77",
                17480 => "HUFF/CDIC (metadata only)",
                _ => $"Unknown ({compression})"
            })
            .Append("Encryption: ").AppendLine(encryption == 0 ? "None" : $"Present ({encryption}); text not extracted");
        if (hasMobi)
        {
            builder.Append("MOBI version: ").AppendLine(mobiVersion.ToString());
        }

        if (encryption == 0 && compression is 1 or 2 && textRecords > 0)
        {
            var targetOutputBytes = checked((int)Math.Min(textLength, (uint)MaximumTextBytes));
            var output = new List<byte>(targetOutputBytes);
            var compressedInput = 0;
            for (var index = 1; index <= textRecords && index < recordCount && output.Count < targetOutputBytes; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recordLength = offsets[index + 1] - offsets[index];
                if (recordLength < 0 || recordLength > MaximumRecordInputBytes - compressedInput)
                {
                    break;
                }
                var recordBytes = ReadRange(stream, offsets[index], checked((int)recordLength));
                var record = recordBytes.AsSpan();
                compressedInput += record.Length;
                if (compression == 1)
                {
                    AppendBounded(output, record, targetOutputBytes);
                }
                else
                {
                    DecompressPalmDoc(record, output, targetOutputBytes, cancellationToken);
                }
            }

            var text = TextPreviewProvider.ConvertHtmlToText(encoding.GetString([.. output]));
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.AppendLine().AppendLine("Text preview:").Append(text);
                if (output.Count >= MaximumTextBytes || textLength > output.Count ||
                    textRecords > recordCount - 1)
                {
                    builder.AppendLine().Append('…');
                }
            }
        }

        return PreviewContent.ForText("Ebook metadata and text", builder.ToString());
    }

    private static byte[] ReadRange(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
        {
            throw new InvalidDataException("Ebook record is outside the file.");
        }
        stream.Position = offset;
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void DecompressPalmDoc(
        ReadOnlySpan<byte> input,
        List<byte> output,
        int maximumOutput,
        CancellationToken cancellationToken)
    {
        var recordOutputStart = output.Count;
        for (var index = 0; index < input.Length && output.Count < maximumOutput;)
        {
            if ((index & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var value = input[index++];
            if (value == 0 || value is >= 9 and <= 0x7F)
            {
                output.Add(value);
            }
            else if (value is >= 1 and <= 8)
            {
                if (value > input.Length - index)
                {
                    throw new InvalidDataException("Truncated PalmDOC literal run.");
                }
                AppendBounded(output, input.Slice(index, value), maximumOutput);
                index += value;
            }
            else if (value >= 0xC0)
            {
                output.Add(0x20);
                if (output.Count < maximumOutput)
                {
                    output.Add((byte)(value ^ 0x80));
                }
            }
            else
            {
                if (index >= input.Length)
                {
                    throw new InvalidDataException("Truncated PalmDOC back-reference.");
                }
                var pair = (value << 8) | input[index++];
                var distance = (pair >> 3) & 0x7FF;
                var length = (pair & 7) + 3;
                if (distance == 0 || distance > output.Count - recordOutputStart)
                {
                    throw new InvalidDataException("Invalid PalmDOC back-reference.");
                }
                for (var copy = 0; copy < length && output.Count < maximumOutput; copy++)
                {
                    output.Add(output[output.Count - distance]);
                }
            }
        }
    }

    private static void AppendBounded(List<byte> output, ReadOnlySpan<byte> bytes, int maximumOutput)
    {
        var count = Math.Min(bytes.Length, maximumOutput - output.Count);
        for (var index = 0; index < count; index++)
        {
            output.Add(bytes[index]);
        }
    }

    private static string DecodePdbName(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]).Trim();
    }

    private static ushort ReadU16(ReadOnlySpan<byte> value, int offset)
    {
        if ((uint)offset > value.Length - 2)
        {
            throw new InvalidDataException("Truncated ebook header.");
        }
        return BinaryPrimitives.ReadUInt16BigEndian(value[offset..]);
    }

    private static int ReadU32(ReadOnlySpan<byte> value, int offset)
    {
        if ((uint)offset > value.Length - 4)
        {
            throw new InvalidDataException("Truncated ebook header.");
        }
        var result = BinaryPrimitives.ReadUInt32BigEndian(value[offset..]);
        return result > int.MaxValue ? throw new InvalidDataException("Ebook offset is too large.") : (int)result;
    }

    private static uint ReadU32Value(ReadOnlySpan<byte> value, int offset)
    {
        if ((uint)offset > value.Length - 4)
        {
            throw new InvalidDataException("Truncated ebook header.");
        }
        return BinaryPrimitives.ReadUInt32BigEndian(value[offset..]);
    }
}
