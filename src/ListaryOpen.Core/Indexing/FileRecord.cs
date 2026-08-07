namespace ListaryOpen.Core.Indexing;

public sealed class FileRecord
{
    private string? _pathKey;
    private string? _name;
    private string? _parentPath;

    private FileRecord(
        string fullPath,
        string? pathKey,
        string? name,
        string? parentPath,
        bool isDirectory,
        long sizeBytes,
        DateTimeOffset lastWriteTime,
        ulong fileReferenceNumber)
    {
        FullPath = fullPath;
        _pathKey = pathKey;
        _name = name;
        _parentPath = parentPath;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
        FileReferenceNumber = fileReferenceNumber;
    }

    public string FullPath { get; }

    public string PathKey => _pathKey ??= FullPath.ToUpperInvariant();

    public string Name => _name ??= GetName(FullPath);

    public string ParentPath => _parentPath ??= GetParentPath(FullPath);

    public bool IsDirectory { get; }

    public long SizeBytes { get; }

    public DateTimeOffset LastWriteTime { get; }

    /// <summary>NTFS MFT segment reference (0 when unknown).</summary>
    public ulong FileReferenceNumber { get; }

    public static FileRecord Create(
        string fullPath,
        bool isDirectory,
        long sizeBytes,
        DateTimeOffset lastWriteTime,
        ulong fileReferenceNumber = 0)
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
        return new FileRecord(
            normalized,
            pathKey: null,
            name: null,
            parentPath: null,
            isDirectory,
            sizeBytes,
            lastWriteTime,
            fileReferenceNumber);
    }

    /// <summary>
    /// Creates a record from a trusted, already-normalized absolute path. This is used by
    /// the raw NTFS scanner, which constructs paths from a normalized volume root and MFT
    /// names. Derived search fields remain lazy so the elevated transport process does not
    /// allocate name, parent, and upper-case path copies that it never serializes.
    /// </summary>
    public static FileRecord CreateFromNormalizedPath(
        string normalizedFullPath,
        bool isDirectory,
        long sizeBytes,
        DateTimeOffset lastWriteTime,
        ulong fileReferenceNumber = 0)
    {
        if (string.IsNullOrWhiteSpace(normalizedFullPath))
        {
            throw new ArgumentException("Path is required.", nameof(normalizedFullPath));
        }

        if (!Path.IsPathFullyQualified(normalizedFullPath))
        {
            throw new ArgumentException("Path must be fully qualified.", nameof(normalizedFullPath));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size cannot be negative.");
        }

        return new FileRecord(
            normalizedFullPath,
            pathKey: null,
            name: null,
            parentPath: null,
            isDirectory,
            sizeBytes,
            lastWriteTime,
            fileReferenceNumber);
    }

    public FileRecord WithFileReferenceNumber(ulong fileReferenceNumber)
    {
        if (fileReferenceNumber == FileReferenceNumber)
        {
            return this;
        }

        return new FileRecord(
            FullPath,
            _pathKey,
            _name,
            _parentPath,
            IsDirectory,
            SizeBytes,
            LastWriteTime,
            fileReferenceNumber);
    }

    private static string GetName(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : Path.GetFileName(normalizedPath);
    }

    private static string GetParentPath(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath) ?? string.Empty;
        return string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : Path.GetDirectoryName(normalizedPath) ?? string.Empty;
    }
}
