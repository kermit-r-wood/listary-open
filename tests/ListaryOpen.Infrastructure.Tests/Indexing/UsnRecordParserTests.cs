using System.Text;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class UsnRecordParserTests
{
    [Fact]
    public void ParseV2ReadsFileNameAndAttributes()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);

        var parsed = UsnRecordParser.ParseV2(buffer);

        Assert.Equal("Invoice.txt", parsed.Name);
        Assert.False(parsed.IsDirectory);
    }

    [Fact]
    public void ParseV2DetectsDirectoryAttribute()
    {
        var name = Encoding.Unicode.GetBytes("Projects");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x10u);

        var parsed = UsnRecordParser.ParseV2(buffer);

        Assert.Equal("Projects", parsed.Name);
        Assert.True(parsed.IsDirectory);
    }

    [Fact]
    public void ParseV2RejectsTooShortRecord()
    {
        var buffer = new byte[59];

        Assert.Throws<ArgumentException>(() => UsnRecordParser.ParseV2(buffer));
    }

    [Fact]
    public void ParseV2RejectsUnsupportedMajorVersion()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);
        BitConverter.GetBytes((ushort)3).CopyTo(buffer, 4);

        Assert.Throws<NotSupportedException>(() => UsnRecordParser.ParseV2(buffer));
    }

    [Fact]
    public void ParseV2RejectsRecordLengthOverflow()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);
        BitConverter.GetBytes((uint)(buffer.Length + 1)).CopyTo(buffer, 0);

        Assert.Throws<ArgumentException>(() => UsnRecordParser.ParseV2(buffer));
    }

    [Fact]
    public void ParseV2RejectsRecordLengthBelowHeader()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);
        BitConverter.GetBytes(59u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 56);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 58);

        Assert.Throws<ArgumentException>(() => UsnRecordParser.ParseV2(buffer));
    }

    [Fact]
    public void ParseV2RejectsFileNameBoundsOverflow()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);
        BitConverter.GetBytes((ushort)(buffer.Length - 2)).CopyTo(buffer, 58);

        Assert.Throws<ArgumentException>(() => UsnRecordParser.ParseV2(buffer));
    }

    [Fact]
    public void ParseV2RejectsFileNameOffsetBelowHeader()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);
        BitConverter.GetBytes((ushort)58).CopyTo(buffer, 58);

        Assert.Throws<ArgumentException>(() => UsnRecordParser.ParseV2(buffer));
    }

    [Fact]
    public void ParseV2RejectsOddFileNameLength()
    {
        var name = Encoding.Unicode.GetBytes("Invoice.txt");
        var buffer = CreateRecordBuffer(name, fileAttributes: 0x80u);
        BitConverter.GetBytes((ushort)(name.Length - 1)).CopyTo(buffer, 56);

        Assert.Throws<ArgumentException>(() => UsnRecordParser.ParseV2(buffer));
    }

    private static byte[] CreateRecordBuffer(byte[] name, uint fileAttributes)
    {
        var buffer = new byte[60 + name.Length];
        BitConverter.GetBytes(buffer.Length).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes(fileAttributes).CopyTo(buffer, 52);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(buffer, 56);
        BitConverter.GetBytes((ushort)60).CopyTo(buffer, 58);
        name.CopyTo(buffer, 60);
        return buffer;
    }
}
