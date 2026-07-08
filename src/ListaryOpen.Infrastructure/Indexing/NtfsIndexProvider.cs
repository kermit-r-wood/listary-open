using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class NtfsIndexProvider : IIndexProvider, INtfsJournalProvider
{
    public const string ProviderName = "NTFS";

    private readonly IElevatedIndexerClient _client;

    public NtfsIndexProvider(IElevatedIndexerClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => ProviderName;

    public bool CanIndex(VolumeInfo volume)
    {
        return volume.IsReady
            && volume.DriveType != System.IO.DriveType.Network
            && string.Equals(volume.FileSystemName, ProviderName, StringComparison.OrdinalIgnoreCase)
            && _client.IsAvailable;
    }

    public IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        return _client.ScanNtfsAsync(root, cancellationToken);
    }

    public Task<UsnJournalState?> QueryJournalStateAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        return _client.QueryJournalStateAsync(root, cancellationToken);
    }

    public IAsyncEnumerable<UsnJournalChange> ReadJournalChangesAsync(
        IndexRoot root,
        ulong expectedUsnJournalId,
        long startUsn,
        long endUsn,
        CancellationToken cancellationToken)
    {
        return _client.ReadJournalChangesAsync(root, expectedUsnJournalId, startUsn, endUsn, cancellationToken);
    }
}
