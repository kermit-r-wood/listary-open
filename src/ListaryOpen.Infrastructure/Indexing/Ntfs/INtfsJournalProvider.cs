using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

internal interface INtfsJournalProvider
{
    Task<UsnJournalState?> QueryJournalStateAsync(IndexRoot root, CancellationToken cancellationToken);

    IAsyncEnumerable<UsnJournalChange> ReadJournalChangesAsync(
        IndexRoot root,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        CancellationToken cancellationToken);
}
