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
    public void CreateNormalizesDirectoryPathWithTrailingSeparator()
    {
        var record = FileRecord.Create("C:\\Users\\Paul\\Documents\\", true, 0, DateTimeOffset.UnixEpoch);

        Assert.Equal("C:\\Users\\Paul\\Documents", record.FullPath);
        Assert.Equal("Documents", record.Name);
        Assert.Equal("C:\\Users\\Paul", record.ParentPath);
        Assert.True(record.IsDirectory);
    }

    [Fact]
    public void CreateUsesDriveRootAsName()
    {
        var record = FileRecord.Create("C:\\", true, 0, DateTimeOffset.UnixEpoch);

        Assert.Equal("C:\\", record.FullPath);
        Assert.Equal("C:", record.Name);
        Assert.Equal(string.Empty, record.ParentPath);
    }

    [Fact]
    public void CreateRejectsEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => FileRecord.Create(" ", false, 0, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void CreateRejectsRelativePath()
    {
        Assert.Throws<ArgumentException>(() => FileRecord.Create("relative.txt", false, 0, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void CreateRejectsDriveRelativePath()
    {
        Assert.Throws<ArgumentException>(() => FileRecord.Create("C:relative.txt", false, 0, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void CreateUsesStableCaseInsensitivePathKey()
    {
        var first = FileRecord.Create("C:\\Docs\\A.txt", false, 1, DateTimeOffset.UnixEpoch);
        var second = FileRecord.Create("c:\\docs\\a.txt", false, 1, DateTimeOffset.UnixEpoch);

        Assert.Equal(first.PathKey, second.PathKey);
        Assert.NotEqual(first.FullPath, second.FullPath);
    }

    [Fact]
    public void CreateRejectsNegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileRecord.Create("C:\\Docs\\A.txt", false, -1, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ConstructorIsNotPublic()
    {
        Assert.Empty(typeof(FileRecord).GetConstructors());
    }
}
