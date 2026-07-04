using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class NtfsIndexProvider : IIndexProvider
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
            && string.Equals(volume.FileSystemName, ProviderName, StringComparison.OrdinalIgnoreCase)
            && _client.IsAvailable;
    }

    public IAsyncEnumerable<FileRecord> ScanAsync(IndexRoot root, CancellationToken cancellationToken)
    {
        return _client.ScanNtfsAsync(root, cancellationToken);
    }
}
