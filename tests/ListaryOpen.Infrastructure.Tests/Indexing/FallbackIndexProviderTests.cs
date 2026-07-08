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

    [Fact]
    public async Task ScanAsyncReturnsHiddenEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        var hiddenDirectory = Path.Combine(root, "HiddenFolder");
        var hiddenFile = Path.Combine(root, "Hidden.txt");
        var nestedFile = Path.Combine(hiddenDirectory, "Nested.txt");

        try
        {
            Directory.CreateDirectory(hiddenDirectory);
            await File.WriteAllTextAsync(hiddenFile, "hidden");
            await File.WriteAllTextAsync(nestedFile, "nested");

            File.SetAttributes(hiddenDirectory, File.GetAttributes(hiddenDirectory) | FileAttributes.Hidden);
            File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);

            var provider = new FallbackIndexProvider();
            var records = new List<FileRecord>();
            await foreach (var record in provider.ScanAsync(new IndexRoot(root), CancellationToken.None))
            {
                records.Add(record);
            }

            Assert.Contains(records, x => x.IsDirectory && x.Name == "HiddenFolder");
            Assert.Contains(records, x => !x.IsDirectory && x.Name == "Hidden.txt");
            Assert.Contains(records, x => !x.IsDirectory && x.Name == "Nested.txt");
        }
        finally
        {
            File.SetAttributes(hiddenFile, FileAttributes.Normal);
            File.SetAttributes(hiddenDirectory, FileAttributes.Directory);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsyncSkipsExcludedDirectorySubtrees()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        try
        {
            var keep = Directory.CreateDirectory(Path.Combine(root, "keep"));
            await File.WriteAllTextAsync(Path.Combine(keep.FullName, "visible.txt"), "visible");
            var excluded = Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            await File.WriteAllTextAsync(Path.Combine(excluded.FullName, "hidden.txt"), "hidden");

            var provider = new FallbackIndexProvider();
            var records = new List<FileRecord>();
            await foreach (var record in provider.ScanAsync(new IndexRoot(root), CancellationToken.None))
            {
                records.Add(record);
            }

            Assert.Contains(records, record => record.FullPath.EndsWith("visible.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(records, record => record.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsyncThrowsWhenCancellationTokenIsAlreadyCanceled()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(root);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try
        {
            var provider = new FallbackIndexProvider();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in provider.ScanAsync(new IndexRoot(root), cancellation.Token))
                {
                }
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScanAsyncThrowsForMissingRootInsteadOfReturningSuccessfulEmptyScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-missing-" + Guid.NewGuid());
        var provider = new FallbackIndexProvider();

        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await foreach (var _ in provider.ScanAsync(new IndexRoot(root), CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public void RootProbeDoesNotIgnoreInaccessibleRoots()
    {
        Assert.False(FallbackIndexProvider.RootProbeIgnoresInaccessibleForTests);
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
}
