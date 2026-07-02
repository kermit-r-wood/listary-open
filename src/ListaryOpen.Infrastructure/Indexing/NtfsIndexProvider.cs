using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class NtfsIndexProvider : IIndexProvider
{
    private readonly IElevatedIndexerClient _client;

    public NtfsIndexProvider(IElevatedIndexerClient client)
    {
        _client = client;
    }

    public string Name => "NTFS";

    public bool CanIndex(VolumeInfo volume)
    {
        return volume.IsReady
            && string.Equals(volume.FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase);
    }

    public IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        return _client.ScanNtfsAsync(root, cancellationToken);
    }
}
