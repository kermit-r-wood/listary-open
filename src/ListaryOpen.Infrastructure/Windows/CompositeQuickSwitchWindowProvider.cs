using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class CompositeQuickSwitchWindowProvider : IRefreshableQuickSwitchWindowProvider
{
    private readonly IReadOnlyList<IQuickSwitchWindowProvider> _providers;
    private readonly IReadOnlyList<QuickSwitchFolderCandidate> _fallbackCandidates;

    public CompositeQuickSwitchWindowProvider(IReadOnlyList<IQuickSwitchWindowProvider> providers)
        : this(providers, Array.Empty<QuickSwitchFolderCandidate>())
    {
    }

    public CompositeQuickSwitchWindowProvider(
        IReadOnlyList<IQuickSwitchWindowProvider> providers,
        IReadOnlyList<QuickSwitchFolderCandidate> fallbackCandidates)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(fallbackCandidates);
        _providers = providers.Where(provider => provider is not null).ToArray();
        _fallbackCandidates = fallbackCandidates.Where(candidate => candidate is not null).ToArray();
    }

    public void Refresh()
    {
        foreach (var provider in _providers.OfType<IRefreshableQuickSwitchWindowProvider>())
        {
            try
            {
                provider.Refresh();
            }
            catch (Exception exception) when (IsExpectedProviderException(exception))
            {
                Trace.TraceError(exception.ToString());
            }
        }
    }

    public IReadOnlyList<QuickSwitchFolderCandidate> GetFolderCandidates()
    {
        var candidates = new List<QuickSwitchFolderCandidate>();
        var candidateIndexesByFolderPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            IReadOnlyList<QuickSwitchFolderCandidate> providerCandidates;
            try
            {
                providerCandidates = provider.GetFolderCandidates();
            }
            catch (Exception exception) when (IsExpectedProviderException(exception))
            {
                Trace.TraceError(exception.ToString());
                continue;
            }

            foreach (var candidate in providerCandidates)
            {
                AddCandidate(candidates, candidateIndexesByFolderPath, candidate);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var candidate in _fallbackCandidates)
            {
                AddCandidate(candidates, candidateIndexesByFolderPath, candidate);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.IsForeground)
            .ToArray();
    }

    private static void AddCandidate(
        List<QuickSwitchFolderCandidate> candidates,
        Dictionary<string, int> candidateIndexesByFolderPath,
        QuickSwitchFolderCandidate candidate)
    {
        var folderPath = NormalizeExistingFolderPath(candidate.FolderPath);
        if (folderPath is null)
        {
            return;
        }

        var normalizedCandidate = candidate with { FolderPath = folderPath };
        if (candidateIndexesByFolderPath.TryGetValue(folderPath, out var candidateIndex))
        {
            if (normalizedCandidate.IsForeground && !candidates[candidateIndex].IsForeground)
            {
                candidates[candidateIndex] = normalizedCandidate;
            }

            return;
        }

        candidateIndexesByFolderPath.Add(folderPath, candidates.Count);
        candidates.Add(normalizedCandidate);
    }

    private static string? NormalizeExistingFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        try
        {
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            return Directory.Exists(normalizedPath) ? normalizedPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or UnauthorizedAccessException
                                          or SecurityException)
        {
            return null;
        }
    }

    private static bool IsExpectedProviderException(Exception exception)
    {
        return exception is ArgumentException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or PathTooLongException
            or UnauthorizedAccessException
            or SecurityException
            or Win32Exception
            or System.Runtime.InteropServices.COMException;
    }
}
