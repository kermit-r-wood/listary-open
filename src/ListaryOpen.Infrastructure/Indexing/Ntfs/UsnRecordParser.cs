using System.Buffers.Binary;
using System.Text;

namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

public sealed record ParsedUsnRecord(string Name, bool IsDirectory);

public static class UsnRecordParser
{
    private const uint FileAttributeDirectory = 0x10;
    private const int UsnRecordV2HeaderLength = 60;

    public static ParsedUsnRecord ParseV2(ReadOnlySpan<byte> record)
    {
        if (record.Length < UsnRecordV2HeaderLength)
        {
            throw new ArgumentException("USN record is too short.", nameof(record));
        }

        var recordLength = BinaryPrimitives.ReadUInt32LittleEndian(record[0..4]);
        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record[4..6]);
        if (majorVersion != 2)
        {
            throw new NotSupportedException("Only USN_RECORD_V2 is supported in version 1.");
        }

        if (recordLength < UsnRecordV2HeaderLength)
        {
            throw new ArgumentException("USN record length is too short.", nameof(record));
        }

        if (recordLength > record.Length)
        {
            throw new ArgumentException("USN record length exceeds buffer length.", nameof(record));
        }

        var fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(record[52..56]);
        var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..58]);
        var fileNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..60]);
        if (fileNameOffset < UsnRecordV2HeaderLength)
        {
            throw new ArgumentException("USN record file name offset is below the V2 header length.", nameof(record));
        }

        if ((fileNameLength & 1) != 0)
        {
            throw new ArgumentException("USN record file name length must be UTF-16 aligned.", nameof(record));
        }

        if (fileNameOffset > recordLength || fileNameLength > recordLength - fileNameOffset)
        {
            throw new ArgumentException("USN record file name exceeds record bounds.", nameof(record));
        }

        var nameBytes = record.Slice(fileNameOffset, fileNameLength);
        var name = Encoding.Unicode.GetString(nameBytes);
        return new ParsedUsnRecord(name, (fileAttributes & FileAttributeDirectory) != 0);
    }
}
