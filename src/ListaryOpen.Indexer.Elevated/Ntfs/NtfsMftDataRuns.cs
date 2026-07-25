using System.Buffers.Binary;

namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal readonly record struct NtfsDataRun(long StartLcn, long ClusterCount, bool IsSparse);

/// <summary>
/// Parses NTFS non-resident attribute data runs and extracts the unnamed $DATA runlist from an MFT record.
/// </summary>
internal static class NtfsMftDataRuns
{
    public static bool TryGetUnnamedDataRuns(ReadOnlySpan<byte> record, out IReadOnlyList<NtfsDataRun> runs, out long validDataLength)
    {
        runs = Array.Empty<NtfsDataRun>();
        validDataLength = 0;

        if (record.Length < 0x30
            || record[0] != (byte)'F'
            || record[1] != (byte)'I'
            || record[2] != (byte)'L'
            || record[3] != (byte)'E')
        {
            return false;
        }

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..0x16]);
        if (firstAttributeOffset < 0x30 || firstAttributeOffset >= record.Length)
        {
            return false;
        }

        var offset = (int)firstAttributeOffset;
        while (offset + 8 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..(offset + 4)]);
            if (attributeType == NtfsMftRecordParser.AttributeEnd)
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
            if (attributeType == NtfsMftRecordParser.AttributeData && nonResident && nameLength == 0)
            {
                if (attribute.Length < 0x40)
                {
                    return false;
                }

                validDataLength = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..0x38]);
                var mappingPairsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..0x22]);
                if (mappingPairsOffset >= attribute.Length)
                {
                    return false;
                }

                if (!TryParseRuns(attribute[mappingPairsOffset..], out var parsedRuns))
                {
                    return false;
                }

                runs = parsedRuns;
                return true;
            }

            offset += (int)attributeLength;
        }

        return false;
    }

    internal static bool TryParseRuns(ReadOnlySpan<byte> mappingPairs, out List<NtfsDataRun> runs)
    {
        runs = new List<NtfsDataRun>();
        long currentLcn = 0;
        var offset = 0;

        while (offset < mappingPairs.Length)
        {
            var header = mappingPairs[offset++];
            if (header == 0)
            {
                return true;
            }

            var lengthSize = header & 0x0F;
            var offsetSize = (header >> 4) & 0x0F;
            if (lengthSize == 0 || offset + lengthSize + offsetSize > mappingPairs.Length)
            {
                runs = new List<NtfsDataRun>();
                return false;
            }

            var clusterCount = ReadLittleEndianInt64(mappingPairs.Slice(offset, lengthSize), signed: false);
            offset += lengthSize;
            if (clusterCount <= 0)
            {
                runs = new List<NtfsDataRun>();
                return false;
            }

            if (offsetSize == 0)
            {
                runs.Add(new NtfsDataRun(StartLcn: 0, clusterCount, IsSparse: true));
                continue;
            }

            var lcnDelta = ReadLittleEndianInt64(mappingPairs.Slice(offset, offsetSize), signed: true);
            offset += offsetSize;
            currentLcn += lcnDelta;
            if (currentLcn < 0)
            {
                runs = new List<NtfsDataRun>();
                return false;
            }

            runs.Add(new NtfsDataRun(currentLcn, clusterCount, IsSparse: false));
        }

        // Missing terminating 0 is tolerated if we consumed the whole buffer.
        return runs.Count > 0;
    }

    private static long ReadLittleEndianInt64(ReadOnlySpan<byte> bytes, bool signed)
    {
        long value = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            value |= (long)bytes[index] << (8 * index);
        }

        if (signed && bytes.Length < 8 && bytes.Length > 0 && (bytes[^1] & 0x80) != 0)
        {
            for (var index = bytes.Length; index < 8; index++)
            {
                value |= (long)0xFF << (8 * index);
            }
        }

        return value;
    }
}
