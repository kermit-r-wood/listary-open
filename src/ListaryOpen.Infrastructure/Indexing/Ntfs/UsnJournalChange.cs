using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Search.NameTable;
// DualWrite removed: USN applies to NameTable and/or Sqlite explicitly.

namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

public enum UsnJournalChangeKind
{
    Upsert,
    Delete,
    FileRename,
    DirectoryRenameOrMove,
    HardLinkResync
}

public sealed record UsnJournalChange(
    UsnJournalChangeKind Kind,
    FileRecord? Record,
    string? FullPath,
    string? OldFullPath,
    ulong FileReferenceNumber = 0,
    IReadOnlyList<FileRecord>? LiveHardLinkRecords = null)
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

    /// <summary>
    /// Replaces all indexed names for an MFT file reference with the live hard-link set.
    /// </summary>
    public static UsnJournalChange HardLinkResync(ulong fileReferenceNumber, IReadOnlyList<FileRecord> liveRecords)
    {
        if (fileReferenceNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileReferenceNumber));
        }

        ArgumentNullException.ThrowIfNull(liveRecords);
        var stamped = liveRecords
            .Select(record => record.WithFileReferenceNumber(fileReferenceNumber))
            .ToArray();
        return new UsnJournalChange(
            UsnJournalChangeKind.HardLinkResync,
            Record: null,
            FullPath: null,
            OldFullPath: null,
            fileReferenceNumber,
            stamped);
    }
}

public enum UsnJournalIndexChangeKind
{
    Upsert,
    Delete,
    HardLinkResync
}

public sealed record UsnJournalIndexChange(
    UsnJournalIndexChangeKind Kind,
    FileRecord? Record,
    string? FullPath,
    ulong FileReferenceNumber = 0,
    IReadOnlyList<FileRecord>? LiveHardLinkRecords = null)
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

    public static UsnJournalIndexChange HardLinkResync(ulong fileReferenceNumber, IReadOnlyList<FileRecord> liveRecords)
    {
        ArgumentNullException.ThrowIfNull(liveRecords);
        return new UsnJournalIndexChange(
            UsnJournalIndexChangeKind.HardLinkResync,
            null,
            null,
            fileReferenceNumber,
            liveRecords);
    }
}

public sealed record UsnJournalApplyResult(
    bool RequiresFullRescan,
    int AppliedCount,
    long NextUsn);

public static class UsnJournalChangeApplier
{
    public static async Task<UsnJournalApplyResult> ApplyAsync(
        SqliteSearchIndex? index,
        IEnumerable<UsnJournalChange> changes,
        UsnJournalCheckpoint nextCheckpoint,
        CancellationToken cancellationToken,
        Func<FileRecord, bool>? recordFilter = null,
        NameTableSearchIndex? nameTable = null)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return await ApplyAsync(
                index,
                ToAsyncEnumerable(changes, cancellationToken),
                nextCheckpoint,
                cancellationToken,
                recordFilter: recordFilter,
                nameTable: nameTable)
            .ConfigureAwait(false);
    }

    public static async Task<UsnJournalApplyResult> ApplyAsync(
        SqliteSearchIndex? index,
        IAsyncEnumerable<UsnJournalChange> changes,
        UsnJournalCheckpoint nextCheckpoint,
        CancellationToken cancellationToken,
        long indexGeneration = 0,
        Func<FileRecord, bool>? recordFilter = null,
        NameTableSearchIndex? nameTable = null)
    {
        if (index is null && nameTable is null)
        {
            throw new ArgumentException("Either sqlite index or nameTable is required.");
        }

        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(nextCheckpoint);

        var materializedChanges = new List<UsnJournalChange>();
        await foreach (var change in changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            materializedChanges.Add(change);
        }

        if (materializedChanges.Any(change => change.Kind == UsnJournalChangeKind.DirectoryRenameOrMove))
        {
            return new UsnJournalApplyResult(
                RequiresFullRescan: true,
                AppliedCount: 0,
                nextCheckpoint.NextUsn);
        }

        var indexChanges = new List<UsnJournalIndexChange>();
        recordFilter ??= _ => true;
        foreach (var change in materializedChanges)
        {
            switch (change.Kind)
            {
                case UsnJournalChangeKind.Upsert:
                    indexChanges.Add(recordFilter(change.Record!)
                        ? UsnJournalIndexChange.Upsert(change.Record!)
                        : UsnJournalIndexChange.Delete(change.Record!.FullPath));
                    break;

                case UsnJournalChangeKind.Delete:
                    indexChanges.Add(UsnJournalIndexChange.Delete(change.FullPath!));
                    break;

                case UsnJournalChangeKind.FileRename:
                    indexChanges.Add(UsnJournalIndexChange.Delete(change.OldFullPath!));
                    if (recordFilter(change.Record!))
                    {
                        indexChanges.Add(UsnJournalIndexChange.Upsert(change.Record!));
                    }
                    break;

                case UsnJournalChangeKind.HardLinkResync:
                {
                    var live = (change.LiveHardLinkRecords ?? Array.Empty<FileRecord>())
                        .Where(recordFilter)
                        .Select(record => record.WithFileReferenceNumber(change.FileReferenceNumber))
                        .ToArray();
                    indexChanges.Add(UsnJournalIndexChange.HardLinkResync(change.FileReferenceNumber, live));
                    break;
                }

                case UsnJournalChangeKind.DirectoryRenameOrMove:
                    throw new InvalidOperationException("Directory rename changes must request a full rescan.");

                default:
                    throw new NotSupportedException($"Unsupported USN change kind: {change.Kind}.");
            }
        }

        if (nameTable is not null)
        {
            // Apply mutations and advance only the in-memory cursor atomically.
            // The coordinator's single durability boundary persists the complete
            // run, avoiding one full LOSN rewrite per USN batch.
            await nameTable
                .ApplyUsnMutationsAsync(indexChanges, nextCheckpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await index!
                .ApplyUsnJournalChangesAsync(indexChanges, nextCheckpoint, cancellationToken, indexGeneration)
                .ConfigureAwait(false);
        }

        return new UsnJournalApplyResult(
            RequiresFullRescan: false,
            AppliedCount: materializedChanges.Count,
            nextCheckpoint.NextUsn);
    }

    private static async IAsyncEnumerable<UsnJournalChange> ToAsyncEnumerable(
        IEnumerable<UsnJournalChange> changes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return change;
            await Task.Yield();
        }
    }
}
