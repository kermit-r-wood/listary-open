using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Indexing;

internal enum LiveIndexChangeKind
{
    Upsert,
    DeletePathAndDescendants
}

internal sealed record LiveIndexChange(
    LiveIndexChangeKind Kind,
    FileRecord? Record,
    string? FullPath)
{
    public static LiveIndexChange Upsert(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new LiveIndexChange(LiveIndexChangeKind.Upsert, record, record.FullPath);
    }

    public static LiveIndexChange DeletePathAndDescendants(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new LiveIndexChange(LiveIndexChangeKind.DeletePathAndDescendants, null, fullPath);
    }
}

internal interface ILiveIndexChangeSink
{
    Task ApplyAsync(IReadOnlyList<LiveIndexChange> changes, CancellationToken cancellationToken);
}

internal sealed class SqliteLiveIndexChangeSink : ILiveIndexChangeSink
{
    private readonly SqliteSearchIndex _index;

    public SqliteLiveIndexChangeSink(SqliteSearchIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
    }

    public Task ApplyAsync(IReadOnlyList<LiveIndexChange> changes, CancellationToken cancellationToken) =>
        _index.ApplyLiveIndexChangesAsync(changes, cancellationToken);
}
