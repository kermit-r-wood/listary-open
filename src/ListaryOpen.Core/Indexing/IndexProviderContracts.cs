namespace ListaryOpen.Core.Indexing;

public sealed record IndexRoot
{
    public IndexRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        var trimmed = path.Trim();
        if (!System.IO.Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Path must be fully qualified.", nameof(path));
        }

        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(trimmed));
    }

    public string Path { get; }
}

public sealed record VolumeInfo(string RootPath, string FileSystemName, bool IsReady);

public sealed record IndexProviderStatus(string ProviderName, string RootPath, bool IsAvailable, string Message);

public interface IIndexProvider
{
    string Name { get; }

    bool CanIndex(VolumeInfo volume);

    IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken);
}
