using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ProviderSelectionTests
{
    [Fact]
    public void SelectProviderPrefersNtfsForReadyNtfsVolume()
    {
        var ntfs = new NtfsIndexProvider(new DisabledElevatedIndexerClient());
        var fallback = new FallbackIndexProvider();
        var indexer = new VolumeIndexer(new IIndexProvider[] { fallback, ntfs });

        var selected = indexer.SelectProvider(new VolumeInfo("C:\\", "NTFS", true));

        Assert.Equal("NTFS", selected.Name);
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
}
