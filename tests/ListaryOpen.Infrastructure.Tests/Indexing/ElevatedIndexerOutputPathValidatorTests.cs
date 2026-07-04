using ListaryOpen.Indexer.Elevated;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerOutputPathValidatorTests
{
    [Fact]
    public void AreAllowedReturnsTrueForListaryTempFiles()
    {
        var recordsPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".err");

        Assert.True(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
    }

    [Fact]
    public void AreAllowedReturnsFalseForNonTempOutputPath()
    {
        var driveRoot = Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";
        var recordsPath = Path.Combine(driveRoot, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".err");

        Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
    }

    [Fact]
    public void AreAllowedReturnsFalseForUnexpectedFileNamePrefix()
    {
        var recordsPath = Path.Combine(Path.GetTempPath(), "records-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".err");

        Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
    }

    [Fact]
    public void AreAllowedReturnsFalseForNestedTempDirectory()
    {
        var nestedDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(nestedDirectory);

        try
        {
            var recordsPath = Path.Combine(nestedDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
            var errorPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".err");

            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            Directory.Delete(nestedDirectory, recursive: true);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForExistingOutputFile()
    {
        var recordsPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".err");
        File.WriteAllText(recordsPath, "existing");

        try
        {
            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            File.Delete(recordsPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForWrongErrorExtension()
    {
        var recordsPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".txt");

        Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
    }
}
