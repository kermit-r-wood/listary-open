using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Search.NameTable;

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

/// <summary>NameTable live FS hints — production path (no SQLite files writes).</summary>
internal sealed class NameTableLiveIndexChangeSink : ILiveIndexChangeSink
{
    private readonly NameTableSearchIndex _nameTable;

    public NameTableLiveIndexChangeSink(NameTableSearchIndex nameTable)
    {
        _nameTable = nameTable ?? throw new ArgumentNullException(nameof(nameTable));
    }

    public async Task ApplyAsync(IReadOnlyList<LiveIndexChange> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (change.Kind)
            {
                case LiveIndexChangeKind.Upsert:
                    await _nameTable.UpsertAsync(change.Record!, cancellationToken).ConfigureAwait(false);
                    break;
                case LiveIndexChangeKind.DeletePathAndDescendants:
                    await _nameTable
                        .DeletePathAndDescendantsAsync(change.FullPath!, cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }
}
