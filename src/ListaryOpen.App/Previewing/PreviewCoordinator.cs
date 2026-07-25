using System.IO;

namespace ListaryOpen.App.Previewing;

internal sealed class PreviewCoordinator
{
    private const int MaximumCachedItems = 32;
    private static readonly TimeSpan SelectionDebounce = TimeSpan.FromMilliseconds(150);
    /// <summary>Default hard timeout for a single provider chain load.</summary>
    internal static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> SystemPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3gp", ".7z", ".aac", ".avi", ".doc", ".docx", ".flac",
        ".gz", ".heic", ".heif", ".m4a", ".m4v", ".mkv", ".mov", ".mp3", ".mp4",
        ".mpeg", ".mpg", ".msg", ".odp", ".ods", ".odt", ".ogg", ".pdf", ".ppt",
        ".pptx", ".rar", ".rtf", ".tar", ".tgz", ".wav",
        ".webm", ".wmv", ".xls", ".xlsx", ".xz"
    };

    private readonly IReadOnlyList<IFilePreviewProvider> _providers;
    private readonly object _cacheGate = new();
    private readonly Dictionary<PreviewCacheKey, LinkedListNode<PreviewCacheEntry>> _cache = [];
    private readonly LinkedList<PreviewCacheEntry> _lru = [];

    internal PreviewCoordinator()
        : this(
        [
            new RasterImagePreviewProvider(),
            new FontPreviewProvider(),
            new SvgPreviewProvider(),
            new DocumentPreviewProvider(),
            new ShellThumbnailPreviewProvider(),
            new ArchivePreviewProvider(),
            new PdfPreviewProvider(),
            new MediaPreviewProvider(),
            new ExecutablePreviewProvider(),
            new TextPreviewProvider()
        ])
    {
    }

    internal PreviewCoordinator(IReadOnlyList<IFilePreviewProvider> providers)
    {
        _providers = providers;
    }

    internal static bool ShouldPreferSystemPreview(PreviewContext context) =>
        SystemPreviewExtensions.Contains(context.Extension);

    internal static Task WaitForSelectionAsync(CancellationToken cancellationToken) =>
        Task.Delay(SelectionDebounce, cancellationToken);

    internal async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cacheKey = new PreviewCacheKey(
            context.FullPath,
            context.SizeBytes,
            context.LastWriteTime.UtcTicks);
        if (TryGetCached(cacheKey, out var cached))
        {
            return cached;
        }

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!provider.CanPreview(context))
            {
                continue;
            }

            try
            {
                // Bound each provider so non-cooperative loads cannot exceed the shared deadline.
                var content = await provider
                    .LoadAsync(context, cancellationToken)
                    .WaitAsync(DefaultLoadTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (content is null)
                {
                    continue;
                }

                AddToCache(cacheKey, content);
                return content;
            }
            catch (TimeoutException)
            {
                // Provider exceeded hard cap; try next provider.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                // A corrupt file or unavailable codec should fall through to the
                // next provider rather than break selection in the search pane.
            }
        }

        return null;
    }

    private bool TryGetCached(PreviewCacheKey key, out PreviewContent? content)
    {
        lock (_cacheGate)
        {
            if (!_cache.TryGetValue(key, out var node))
            {
                content = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            content = node.Value.Content;
            return true;
        }
    }

    private void AddToCache(PreviewCacheKey key, PreviewContent content)
    {
        lock (_cacheGate)
        {
            if (_cache.Remove(key, out var existing))
            {
                _lru.Remove(existing);
            }

            var node = _lru.AddFirst(new PreviewCacheEntry(key, content));
            _cache[key] = node;
            while (_cache.Count > MaximumCachedItems && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or
            NotSupportedException or ArgumentException or FormatException or OverflowException or
            System.Runtime.InteropServices.COMException or System.Xml.XmlException;

    private sealed record PreviewCacheKey(string Path, long SizeBytes, long LastWriteTicks);

    private sealed record PreviewCacheEntry(PreviewCacheKey Key, PreviewContent Content);
}
