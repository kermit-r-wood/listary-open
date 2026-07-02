using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Tests.Indexing;

public sealed class FileRecordTests
{
    [Fact]
    public void CreateNormalizesPathAndName()
    {
        var record = FileRecord.Create("C:\\Users\\Paul\\Documents\\Report.docx", false, 123, new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero));

        Assert.Equal("C:\\Users\\Paul\\Documents\\Report.docx", record.FullPath);
        Assert.Equal("Report.docx", record.Name);
        Assert.Equal("C:\\Users\\Paul\\Documents", record.ParentPath);
        Assert.False(record.IsDirectory);
        Assert.Equal(123, record.SizeBytes);
    }

    [Fact]
    public void CreateRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => FileRecord.Create(" ", false, 0, DateTimeOffset.UnixEpoch));
    }
}
