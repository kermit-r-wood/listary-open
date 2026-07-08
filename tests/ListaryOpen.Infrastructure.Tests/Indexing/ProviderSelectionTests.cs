using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ProviderSelectionTests
{
    [Fact]
    public void NtfsIndexProviderConstructorThrowsForNullClient()
    {
        Assert.Throws<ArgumentNullException>("client", () => new NtfsIndexProvider(null!));
    }

    [Fact]
    public void VolumeIndexerConstructorThrowsForNullProviders()
    {
        Assert.Throws<ArgumentNullException>("providers", () => new VolumeIndexer(null!));
    }

    [Fact]
    public void VolumeIndexerConstructorThrowsForNullProviderEntry()
    {
        var providers = new IIndexProvider?[] { new FallbackIndexProvider(), null };

        Assert.Throws<ArgumentNullException>("providers", () => new VolumeIndexer(providers!));
    }

    [Fact]
    public void SelectProviderPrefersNtfsForReadyNtfsVolume()
    {
        var ntfs = new NtfsIndexProvider(new RecordingElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { fallback, ntfs });

        var selected = indexer.SelectProvider(new VolumeInfo("C:\\", "NTFS", true));

        Assert.Equal("NTFS", selected.Name);
    }

    [Fact]
    public void SelectProviderFallsBackForReadyNtfsVolumeWhenElevatedClientIsUnavailable()
    {
        var ntfs = new NtfsIndexProvider(new DisabledElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { fallback, ntfs });

        var selected = indexer.SelectProvider(new VolumeInfo("C:\\", "NTFS", true));

        Assert.Equal("Fallback", selected.Name);
    }

    [Fact]
    public void SelectProviderFallsBackForNonNtfsVolume()
    {
        var ntfs = new NtfsIndexProvider(new DisabledElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { ntfs, fallback });

        var selected = indexer.SelectProvider(new VolumeInfo("Z:\\", "exFAT", true));

        Assert.Equal("Fallback", selected.Name);
    }

    [Fact]
    public void SelectProviderFallsBackForMappedNetworkDriveEvenWhenFormatReportsNtfs()
    {
        var ntfs = new NtfsIndexProvider(new RecordingElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { ntfs, fallback });

        var selected = indexer.SelectProvider(new VolumeInfo("Z:\\", "NTFS", true, DriveType.Network));

        Assert.Equal("Fallback", selected.Name);
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("ntfs")]
    [InlineData("NtFs")]
    public void NtfsIndexProviderCanIndexMatchesNtfsCaseInsensitively(string fileSystemName)
    {
        var provider = new NtfsIndexProvider(new RecordingElevatedIndexerClient());

        var canIndex = provider.CanIndex(new VolumeInfo("C:\\", fileSystemName, true));

        Assert.True(canIndex);
    }

    [Fact]
    public void NtfsIndexProviderCanIndexReturnsFalseWhenElevatedClientIsUnavailable()
    {
        var provider = new NtfsIndexProvider(new DisabledElevatedIndexerClient());

        var canIndex = provider.CanIndex(new VolumeInfo("C:\\", "NTFS", true));

        Assert.False(canIndex);
    }

    [Fact]
    public void NtfsIndexProviderCanIndexReturnsFalseWhenNtfsVolumeIsNotReady()
    {
        var provider = new NtfsIndexProvider(new DisabledElevatedIndexerClient());

        var canIndex = provider.CanIndex(new VolumeInfo("C:\\", "NTFS", false));

        Assert.False(canIndex);
    }

    [Fact]
    public void NtfsIndexProviderScanAsyncDelegatesToElevatedClient()
    {
        var client = new RecordingElevatedIndexerClient();
        var provider = new NtfsIndexProvider(client);
        var root = new IndexRoot("C:\\");
        using var cancellation = new CancellationTokenSource();

        var scan = provider.ScanAsync(root, cancellation.Token);

        Assert.Same(root, client.Root);
        Assert.Equal(cancellation.Token, client.CancellationToken);
        Assert.Same(client.ScanResult, scan);
    }

    [Fact]
    public async Task DisabledElevatedIndexerClientScanNtfsAsyncThrowsWhenCancellationTokenIsAlreadyCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var client = new DisabledElevatedIndexerClient();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.ScanNtfsAsync(new IndexRoot("C:\\"), cancellation.Token))
            {
            }
        });
    }

    private sealed class RecordingElevatedIndexerClient : IElevatedIndexerClient
    {
        public bool IsAvailable => true;

        public IndexRoot? Root { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public IAsyncEnumerable<FileRecord> ScanResult { get; } = Empty();

        public IAsyncEnumerable<FileRecord> ScanNtfsAsync(IndexRoot root, CancellationToken cancellationToken)
        {
            Root = root;
            CancellationToken = cancellationToken;
            return ScanResult;
        }

        private static async IAsyncEnumerable<FileRecord> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
