using System.Buffers.Binary;
using System.Text;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal enum NtfsFileNameNamespace : byte
{
    Posix = 0,
    Win32 = 1,
    Dos = 2,
    Win32AndDos = 3
}

internal readonly record struct NtfsMftFileName(
    ulong ParentFileReferenceNumber,
    string Name,
    NtfsFileNameNamespace Namespace);

internal sealed record NtfsMftParsedRecord(
    ulong RecordNumber,
    bool InUse,
    bool IsDirectory,
    bool IsBaseRecord,
    ulong BaseFileReferenceNumber,
    long SizeBytes,
    DateTimeOffset LastWriteTime,
    IReadOnlyList<NtfsMftFileName> FileNames,
    IReadOnlyList<ulong> AttributeListFileReferences);

/// <summary>
/// Parses a single NTFS FILE record from a raw MFT segment (after multi-sector fixup).
/// Supports resident attributes needed for full-volume indexing and hard-link expansion.
/// </summary>
internal static class NtfsMftRecordParser
{
    internal const uint AttributeStandardInformation = 0x10;
    internal const uint AttributeAttributeList = 0x20;
    internal const uint AttributeFileName = 0x30;
    internal const uint AttributeData = 0x80;
    internal const uint AttributeEnd = 0xFFFF_FFFF;

    private const int FileRecordHeaderLength = 0x30;
    private const ulong MftSegmentReferenceNumberMask = 0x0000_FFFF_FFFF_FFFF;

    public static bool TryParse(ReadOnlySpan<byte> record, ulong fallbackRecordNumber, out NtfsMftParsedRecord parsed)
    {
        parsed = null!;
        if (record.Length < FileRecordHeaderLength)
        {
            return false;
        }

        if (record[0] != (byte)'F'
            || record[1] != (byte)'I'
            || record[2] != (byte)'L'
            || record[3] != (byte)'E')
        {
            return false;
        }

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..0x16]);
        if (firstAttributeOffset < FileRecordHeaderLength || firstAttributeOffset >= record.Length)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..0x18]);
        var inUse = (flags & 0x0001) != 0;
        var isDirectory = (flags & 0x0002) != 0;
        var baseFileReference = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..0x28]);
        var isBaseRecord = baseFileReference == 0;

        var recordNumber = fallbackRecordNumber;
        if (record.Length >= 0x30)
        {
            var headerRecordNumber = BinaryPrimitives.ReadUInt32LittleEndian(record[0x2C..0x30]);
            if (headerRecordNumber != 0)
            {
                recordNumber = headerRecordNumber;
            }
        }

        if (!inUse)
        {
            parsed = new NtfsMftParsedRecord(
                recordNumber,
                InUse: false,
                isDirectory,
                isBaseRecord,
                baseFileReference,
                SizeBytes: 0,
                LastWriteTime: default,
                Array.Empty<NtfsMftFileName>(),
                Array.Empty<ulong>());
            return true;
        }

        long sizeBytes = 0;
        var hasSize = false;
        DateTimeOffset lastWriteTime = default;
        var hasLastWriteTime = false;
        List<NtfsMftFileName>? fileNames = null;
        List<ulong>? attributeListReferences = null;

        var offset = (int)firstAttributeOffset;
        while (offset + 8 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..(offset + 4)]);
            if (attributeType == AttributeEnd)
            {
                break;
            }

            var attributeLength = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..(offset + 8)]);
            if (attributeLength < 16 || offset + attributeLength > record.Length)
            {
                break;
            }

            var attribute = record.Slice(offset, (int)attributeLength);
            var nonResident = attribute[0x08] != 0;
            var nameLength = attribute[0x09];

            switch (attributeType)
            {
                case AttributeStandardInformation when !nonResident:
                    if (TryReadResidentValue(attribute, out var stdInfo)
                        && stdInfo.Length >= 24)
                    {
                        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(stdInfo[0x08..0x10]);
                        if (TryFromFileTimeUtc(fileTime, out lastWriteTime))
                        {
                            hasLastWriteTime = true;
                        }
                    }

                    break;

                case AttributeFileName when !nonResident:
                    if (TryReadResidentValue(attribute, out var fileNameValue)
                        && TryParseFileNameAttribute(fileNameValue, out var fileName))
                    {
                        (fileNames ??= new List<NtfsMftFileName>(2)).Add(fileName);
                    }

                    break;

                case AttributeData when nameLength == 0:
                    if (!nonResident)
                    {
                        if (TryReadResidentValue(attribute, out var residentData))
                        {
                            sizeBytes = residentData.Length;
                            hasSize = true;
                        }
                    }
                    else if (attribute.Length >= 0x40)
                    {
                        // Non-resident DATA: real size at +0x30.
                        sizeBytes = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..0x38]);
                        if (sizeBytes < 0)
                        {
                            sizeBytes = 0;
                        }

                        hasSize = true;
                    }

                    break;

                case AttributeAttributeList when !nonResident:
                    if (TryReadResidentValue(attribute, out var listValue))
                    {
                        CollectAttributeListFileReferences(
                            listValue,
                            attributeListReferences ??= new List<ulong>());
                    }

                    break;
            }

            offset += (int)attributeLength;
        }

        if (isDirectory)
        {
            sizeBytes = 0;
            hasSize = true;
        }

        if (!hasLastWriteTime)
        {
            lastWriteTime = DateTimeOffset.UnixEpoch;
        }

        if (!hasSize)
        {
            sizeBytes = 0;
        }

        parsed = new NtfsMftParsedRecord(
            recordNumber,
            InUse: true,
            isDirectory,
            isBaseRecord,
            baseFileReference,
            sizeBytes,
            lastWriteTime,
            fileNames is null ? Array.Empty<NtfsMftFileName>() : fileNames,
            attributeListReferences is null ? Array.Empty<ulong>() : attributeListReferences);
        return true;
    }

    /// <summary>
    /// Applies the NTFS multi-sector update sequence (USA) fixup in-place.
    /// </summary>
    public static bool TryApplyUpdateSequence(Span<byte> record, int bytesPerSector)
    {
        if (bytesPerSector < 512 || record.Length < FileRecordHeaderLength)
        {
            return false;
        }

        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..0x06]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..0x08]);
        if (usaOffset == 0 || usaCount < 2)
        {
            return true;
        }

        var usaLengthBytes = usaCount * sizeof(ushort);
        if (usaOffset + usaLengthBytes > record.Length)
        {
            return false;
        }

        var sectorCount = usaCount - 1;
        if (sectorCount * bytesPerSector > record.Length)
        {
            return false;
        }

        for (var sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
        {
            var sectorEnd = ((sectorIndex + 1) * bytesPerSector) - sizeof(ushort);
            var usaValueOffset = usaOffset + ((sectorIndex + 1) * sizeof(ushort));
            var replacement = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaValueOffset, sizeof(ushort)));
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(sectorEnd, sizeof(ushort)), replacement);
        }

        return true;
    }

    public static ulong GetMftSegmentReferenceNumber(ulong fileReferenceNumber)
        => fileReferenceNumber & MftSegmentReferenceNumberMask;

    public static IEnumerable<NtfsMftFileName> SelectIndexableFileNames(IEnumerable<NtfsMftFileName> fileNames)
    {
        // Prefer Win32 / Win32+DOS names. Skip pure DOS 8.3 short names to avoid noise.
        // Keep POSIX only when no Win32 name exists for that parent+name pair.
        var list = fileNames as IReadOnlyList<NtfsMftFileName> ?? fileNames.ToList();
        if (list.Count == 0)
        {
            return list;
        }

        var hasWin32 = list.Any(name =>
            name.Namespace is NtfsFileNameNamespace.Win32 or NtfsFileNameNamespace.Win32AndDos);
        if (!hasWin32)
        {
            return list.Where(name => name.Namespace != NtfsFileNameNamespace.Dos);
        }

        return list.Where(name =>
            name.Namespace is NtfsFileNameNamespace.Win32 or NtfsFileNameNamespace.Win32AndDos);
    }

    private static bool TryParseFileNameAttribute(ReadOnlySpan<byte> value, out NtfsMftFileName fileName)
    {
        fileName = default;
        if (value.Length < 0x42)
        {
            return false;
        }

        var parent = BinaryPrimitives.ReadUInt64LittleEndian(value[0x00..0x08]);
        var nameLengthChars = value[0x40];
        var nameNamespace = (NtfsFileNameNamespace)value[0x41];
        var nameBytes = nameLengthChars * 2;
        if (0x42 + nameBytes > value.Length)
        {
            return false;
        }

        var name = Encoding.Unicode.GetString(value.Slice(0x42, nameBytes));
        if (string.IsNullOrEmpty(name) || name == "." || name == "..")
        {
            return false;
        }

        fileName = new NtfsMftFileName(parent, name, nameNamespace);
        return true;
    }

    private static void CollectAttributeListFileReferences(ReadOnlySpan<byte> listValue, List<ulong> references)
    {
        var offset = 0;
        while (offset + 26 <= listValue.Length)
        {
            var entryLength = BinaryPrimitives.ReadUInt16LittleEndian(listValue[(offset + 4)..(offset + 6)]);
            if (entryLength < 26 || offset + entryLength > listValue.Length)
            {
                break;
            }

            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(listValue[offset..(offset + 4)]);
            var fileReference = BinaryPrimitives.ReadUInt64LittleEndian(listValue[(offset + 16)..(offset + 24)]);
            if (attributeType is AttributeFileName or AttributeStandardInformation or AttributeData)
            {
                var segment = GetMftSegmentReferenceNumber(fileReference);
                if (segment != 0 && !references.Contains(segment))
                {
                    references.Add(segment);
                }
            }

            offset += entryLength;
        }
    }

    private static bool TryReadResidentValue(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (attribute.Length < 0x18)
        {
            return false;
        }

        var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..0x14]);
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..0x16]);
        if (valueOffset + valueLength > attribute.Length)
        {
            return false;
        }

        value = attribute.Slice(valueOffset, (int)valueLength);
        return true;
    }

    private static bool TryFromFileTimeUtc(long fileTime, out DateTimeOffset timestamp)
    {
        try
        {
            if (fileTime <= 0)
            {
                timestamp = default;
                return false;
            }

            timestamp = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }
}
