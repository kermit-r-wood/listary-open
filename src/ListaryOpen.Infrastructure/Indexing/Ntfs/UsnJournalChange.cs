using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

public enum UsnJournalChangeKind
{
    Upsert,
    Delete,
    FileRename,
    DirectoryRenameOrMove
}

public sealed record UsnJournalChange(
    UsnJournalChangeKind Kind,
    FileRecord? Record,
    string? FullPath,
    string? OldFullPath)
{
    public static UsnJournalChange Upsert(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new UsnJournalChange(UsnJournalChangeKind.Upsert, record, record.FullPath, null);
    }

    public static UsnJournalChange Delete(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new UsnJournalChange(UsnJournalChangeKind.Delete, null, fullPath, null);
    }

    public static UsnJournalChange FileRename(string oldFullPath, FileRecord newRecord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFullPath);
        ArgumentNullException.ThrowIfNull(newRecord);
        return new UsnJournalChange(UsnJournalChangeKind.FileRename, newRecord, newRecord.FullPath, oldFullPath);
    }

    public static UsnJournalChange DirectoryRenameOrMove()
        => new(UsnJournalChangeKind.DirectoryRenameOrMove, null, null, null);
}

public enum UsnJournalIndexChangeKind
{
    Upsert,
    Delete
}

public sealed record UsnJournalIndexChange(
    UsnJournalIndexChangeKind Kind,
    FileRecord? Record,
    string? FullPath)
{
    public static UsnJournalIndexChange Upsert(FileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new UsnJournalIndexChange(UsnJournalIndexChangeKind.Upsert, record, record.FullPath);
    }

    public static UsnJournalIndexChange Delete(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return new UsnJournalIndexChange(UsnJournalIndexChangeKind.Delete, null, fullPath);
    }
}

public sealed record UsnJournalApplyResult(
    bool RequiresFullRescan,
    int AppliedCount,
    long NextUsn);

public static class UsnJournalChangeApplier
{
    public static async Task<UsnJournalApplyResult> ApplyAsync(
        SqliteSearchIndex index,
        IEnumerable<UsnJournalChange> changes,
        UsnJournalCheckpoint nextCheckpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(nextCheckpoint);

        var materializedChanges = changes.ToArray();
        if (materializedChanges.Any(change => change.Kind == UsnJournalChangeKind.DirectoryRenameOrMove))
        {
            return new UsnJournalApplyResult(
                RequiresFullRescan: true,
                AppliedCount: 0,
                nextCheckpoint.NextUsn);
        }

        var indexChanges = new List<UsnJournalIndexChange>();
        foreach (var change in materializedChanges)
        {
            switch (change.Kind)
            {
                case UsnJournalChangeKind.Upsert:
                    indexChanges.Add(UsnJournalIndexChange.Upsert(change.Record!));
                    break;

                case UsnJournalChangeKind.Delete:
                    indexChanges.Add(UsnJournalIndexChange.Delete(change.FullPath!));
                    break;

                case UsnJournalChangeKind.FileRename:
                    indexChanges.Add(UsnJournalIndexChange.Delete(change.OldFullPath!));
                    indexChanges.Add(UsnJournalIndexChange.Upsert(change.Record!));
                    break;

                case UsnJournalChangeKind.DirectoryRenameOrMove:
                    throw new InvalidOperationException("Directory rename changes must request a full rescan.");

                default:
                    throw new NotSupportedException($"Unsupported USN change kind: {change.Kind}.");
            }
        }

        await index
            .ApplyUsnJournalChangesAsync(indexChanges, nextCheckpoint, cancellationToken)
            .ConfigureAwait(false);

        return new UsnJournalApplyResult(
            RequiresFullRescan: false,
            AppliedCount: materializedChanges.Length,
            nextCheckpoint.NextUsn);
    }
}
