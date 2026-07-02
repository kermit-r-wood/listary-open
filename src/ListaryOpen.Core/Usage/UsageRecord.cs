namespace ListaryOpen.Core.Usage;

public sealed record UsageRecord
{
    public UsageRecord(string fullPath, int openCount, DateTimeOffset lastUsedAt)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Path is required.", nameof(fullPath));
        }

        if (openCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openCount), "Open count cannot be negative.");
        }

        var trimmed = fullPath.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Path must be fully qualified.", nameof(fullPath));
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));

        FullPath = normalized;
        PathKey = normalized.ToUpperInvariant();
        OpenCount = openCount;
        LastUsedAt = lastUsedAt;
    }

    public string FullPath { get; }

    public string PathKey { get; }

    public int OpenCount { get; }

    public DateTimeOffset LastUsedAt { get; }
}
