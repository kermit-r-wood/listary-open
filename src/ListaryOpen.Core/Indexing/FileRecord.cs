namespace ListaryOpen.Core.Indexing;

public sealed class FileRecord
{
    private FileRecord(
        string fullPath,
        string pathKey,
        string name,
        string parentPath,
        bool isDirectory,
        long sizeBytes,
        DateTimeOffset lastWriteTime)
    {
        FullPath = fullPath;
        PathKey = pathKey;
        Name = name;
        ParentPath = parentPath;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
    }

    public string FullPath { get; }

    public string PathKey { get; }

    public string Name { get; }

    public string ParentPath { get; }

    public bool IsDirectory { get; }

    public long SizeBytes { get; }

    public DateTimeOffset LastWriteTime { get; }

    public static FileRecord Create(string fullPath, bool isDirectory, long sizeBytes, DateTimeOffset lastWriteTime)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Path is required.", nameof(fullPath));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size cannot be negative.");
        }

        var trimmed = fullPath.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Path must be fully qualified.", nameof(fullPath));
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var isRoot = string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase);
        var name = isRoot ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : Path.GetFileName(normalized);
        var parent = isRoot ? string.Empty : Path.GetDirectoryName(normalized) ?? string.Empty;
        var pathKey = normalized.ToUpperInvariant();

        return new FileRecord(normalized, pathKey, name, parent, isDirectory, sizeBytes, lastWriteTime);
    }
}
