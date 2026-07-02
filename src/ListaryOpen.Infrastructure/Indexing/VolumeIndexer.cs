using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class VolumeIndexer
{
    private readonly IReadOnlyList<IIndexProvider> _providers;

    public VolumeIndexer(IEnumerable<IIndexProvider> providers)
    {
        _providers = providers.ToArray();
    }

    public IIndexProvider SelectProvider(VolumeInfo volume)
    {
        return _providers
            .OrderByDescending(provider => provider.Name == "NTFS")
            .First(provider => provider.CanIndex(volume));
    }
}
