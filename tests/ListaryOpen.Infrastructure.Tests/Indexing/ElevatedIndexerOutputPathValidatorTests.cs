using ListaryOpen.Indexer.Elevated;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerOutputPathValidatorTests
{
    [Fact]
    public void AreAllowedReturnsTrueForListaryTempFiles()
    {
        var allowedTempDirectory = CreateAllowedTempDirectory();
        var recordsPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".err");

        try
        {
            Assert.True(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            DeleteFileIfExists(recordsPath);
            DeleteFileIfExists(errorPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForNonTempOutputPath()
    {
        var allowedTempDirectory = CreateAllowedTempDirectory();
        var driveRoot = Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\";
        var recordsPath = Path.Combine(driveRoot, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".err");

        try
        {
            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            DeleteFileIfExists(errorPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForUnexpectedFileNamePrefix()
    {
        var allowedTempDirectory = CreateAllowedTempDirectory();
        var recordsPath = Path.Combine(allowedTempDirectory, "records-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".err");

        try
        {
            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            DeleteFileIfExists(recordsPath);
            DeleteFileIfExists(errorPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForNestedTempDirectory()
    {
        var allowedTempDirectory = CreateAllowedTempDirectory();
        var nestedDirectory = Path.Combine(allowedTempDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        var recordsPath = Path.Combine(nestedDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".err");

        try
        {
            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            DeleteFileIfExists(recordsPath);
            DeleteFileIfExists(errorPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForExistingOutputFile()
    {
        var allowedTempDirectory = CreateAllowedTempDirectory();
        var recordsPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".err");

        try
        {
            File.WriteAllText(recordsPath, "existing");

            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            DeleteFileIfExists(recordsPath);
            DeleteFileIfExists(errorPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForWrongErrorExtension()
    {
        var allowedTempDirectory = CreateAllowedTempDirectory();
        var recordsPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        var errorPath = Path.Combine(allowedTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".txt");

        try
        {
            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            DeleteFileIfExists(recordsPath);
            DeleteFileIfExists(errorPath);
        }
    }

    [Fact]
    public void AreAllowedReturnsFalseForCallerChosenDirectoryEvenWithMatchingFileNames()
    {
        var callerChosenDirectory = Directory.CreateTempSubdirectory("listary-open-indexer-untrusted-").FullName;

        try
        {
            var recordsPath = Path.Combine(callerChosenDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
            var errorPath = Path.Combine(callerChosenDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".err");

            Assert.False(ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath));
        }
        finally
        {
            Directory.Delete(callerChosenDirectory, recursive: true);
        }
    }

    private static string CreateAllowedTempDirectory()
    {
        var trustedTempDirectory = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(trustedTempDirectory);
        return trustedTempDirectory;
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
