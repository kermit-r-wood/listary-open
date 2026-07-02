using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class VolumeIndexer
{
    private readonly IReadOnlyList<IIndexProvider> _providers;

    public VolumeIndexer(IEnumerable<IIndexProvider> providers)
    {
        if (providers is null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        _providers = providers.Select(provider => provider ?? throw new ArgumentNullException(nameof(providers))).ToArray();
    }

    public IIndexProvider SelectProvider(VolumeInfo volume)
    {
        return _providers
            .OrderByDescending(provider => provider.Name == NtfsIndexProvider.ProviderName)
            .First(provider => provider.CanIndex(volume));
    }
}
