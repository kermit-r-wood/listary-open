namespace ListaryOpen.Core.Indexing;

public sealed record FileRecord(
    string FullPath,
    string Name,
    string ParentPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastWriteTime)
{
    public static FileRecord Create(string fullPath, bool isDirectory, long sizeBytes, DateTimeOffset lastWriteTime)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Path is required.", nameof(fullPath));
        }

        var normalized = Path.GetFullPath(fullPath.Trim());
        var name = Path.GetFileName(normalized);
        var parent = Path.GetDirectoryName(normalized) ?? string.Empty;
        return new FileRecord(normalized, name, parent, isDirectory, sizeBytes, lastWriteTime);
    }
}
