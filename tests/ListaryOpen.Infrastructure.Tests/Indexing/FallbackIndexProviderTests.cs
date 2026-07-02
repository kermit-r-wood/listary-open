using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class FallbackIndexProviderTests
{
    [Fact]
    public async Task ScanAsyncReturnsFilesAndFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "Nested", "Invoice.txt"), "test");

            var provider = new FallbackIndexProvider();
            var records = new List<FileRecord>();
            await foreach (var record in provider.ScanAsync(new IndexRoot(root), CancellationToken.None))
            {
                records.Add(record);
            }

            Assert.Contains(records, x => x.IsDirectory && x.Name == "Nested");
            Assert.Contains(records, x => !x.IsDirectory && x.Name == "Invoice.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void CanIndexReturnsVolumeReadiness(bool isReady, bool expected)
    {
        var provider = new FallbackIndexProvider();

        var canIndex = provider.CanIndex(new VolumeInfo("C:\\", "NTFS", isReady));

        Assert.Equal(expected, canIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative")]
    public void IndexRootRejectsInvalidPaths(string path)
    {
        Assert.Throws<ArgumentException>(() => new IndexRoot(path));
    }

    [Fact]
    public void IndexRootNormalizesPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid(), ".");

        var indexRoot = new IndexRoot(root);

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), indexRoot.Path);
    }
}
