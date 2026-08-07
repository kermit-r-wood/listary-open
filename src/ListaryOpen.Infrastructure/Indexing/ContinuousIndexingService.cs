using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading.Channels;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Indexing;

internal enum FileChangeHintKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

internal sealed record FileChangeHint(FileChangeHintKind Kind, string FullPath, string? OldFullPath = null);

internal interface IIndexRootWatcher : IDisposable
{
    void Start();
}

internal interface IIndexRootWatcherFactory
{
    IIndexRootWatcher Create(
        string rootPath,
        Action<FileChangeHint> onChange,
        Action<Exception> onError);
}

internal sealed class FileSystemIndexRootWatcherFactory : IIndexRootWatcherFactory
{
    public IIndexRootWatcher Create(
        string rootPath,
        Action<FileChangeHint> onChange,
        Action<Exception> onError) =>
        new FileSystemIndexRootWatcher(rootPath, onChange, onError);

    private sealed class FileSystemIndexRootWatcher : IIndexRootWatcher
    {
        private readonly FileSystemWatcher _watcher;
        private readonly Action<FileChangeHint> _onChange;
        private readonly Action<Exception> _onError;

        public FileSystemIndexRootWatcher(
            string rootPath,
            Action<FileChangeHint> onChange,
            Action<Exception> onError)
        {
            _onChange = onChange;
            _onError = onError;
            _watcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 32 * 1024,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size
            };
            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }

        public void Start() => _watcher.EnableRaisingEvents = true;

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Changed -= OnChanged;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
        }

        private void OnCreated(object sender, FileSystemEventArgs args) =>
            _onChange(new FileChangeHint(FileChangeHintKind.Created, args.FullPath));

        private void OnChanged(object sender, FileSystemEventArgs args) =>
            _onChange(new FileChangeHint(FileChangeHintKind.Changed, args.FullPath));

        private void OnDeleted(object sender, FileSystemEventArgs args) =>
            _onChange(new FileChangeHint(FileChangeHintKind.Deleted, args.FullPath));

        private void OnRenamed(object sender, RenamedEventArgs args) =>
            _onChange(new FileChangeHint(FileChangeHintKind.Renamed, args.FullPath, args.OldFullPath));

        private void OnError(object sender, ErrorEventArgs args) =>
            _onError(args.GetException());
    }
}

/// <summary>
/// Applies low-latency file-system hints to the search index. Watcher events are
/// deliberately treated as hints: overflow requests a checkpoint-based
/// reconciliation, while normal bursts are coalesced into one bounded worker.
/// </summary>
public sealed class ContinuousIndexingService : IDisposable
{
    private const int QueueCapacity = 4096;
    private const int ApplyBatchSize = 500;
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);
    /// <summary>Do not block app exit on a stuck apply batch forever.</summary>
    internal static readonly TimeSpan DisposeWorkerTimeout = TimeSpan.FromSeconds(2);

    private readonly object _watcherGate = new();
    private readonly Channel<FileChangeHint> _hints;
    private readonly ILiveIndexChangeSink _sink;
    private readonly IIndexRootWatcherFactory _watcherFactory;
    private readonly Action _requestReconciliation;
    private readonly TimeSpan _debounce;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;
    private readonly List<IIndexRootWatcher> _watchers = new();
    private volatile PathFilterSnapshot _filter;
    private TaskCompletionSource _baselineReady = CreateReadySource();
    private int _reconciliationRequested;
    private bool _started;
    private bool _disposed;

    public ContinuousIndexingService(
        SqliteSearchIndex index,
        IReadOnlyList<string> indexedRoots,
        IReadOnlyList<string> configuredExclusions,
        IReadOnlyList<string> internalExcludedPaths,
        Action requestReconciliation)
        : this(
            new SqliteLiveIndexChangeSink(index),
            indexedRoots,
            configuredExclusions,
            internalExcludedPaths,
            requestReconciliation,
            new FileSystemIndexRootWatcherFactory(),
            DefaultDebounce)
    {
    }

    internal ContinuousIndexingService(
        ILiveIndexChangeSink sink,
        IReadOnlyList<string> indexedRoots,
        IReadOnlyList<string> configuredExclusions,
        IReadOnlyList<string> internalExcludedPaths,
        Action requestReconciliation,
        IIndexRootWatcherFactory watcherFactory,
        TimeSpan debounce)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _watcherFactory = watcherFactory ?? throw new ArgumentNullException(nameof(watcherFactory));
        _requestReconciliation = requestReconciliation ?? throw new ArgumentNullException(nameof(requestReconciliation));
        if (debounce < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        _debounce = debounce;
        _filter = PathFilterSnapshot.Create(indexedRoots, configuredExclusions, internalExcludedPaths);
        _hints = Channel.CreateBounded<FileChangeHint>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ProcessHintsAsync);
    }

    public void Start(bool baselineReady = false)
    {
        ThrowIfDisposed();
        lock (_watcherGate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            CreateAndStartWatchers(_filter.Roots);
        }

        if (baselineReady)
        {
            MarkBaselineReady();
        }
    }

    public void Reconfigure(
        IReadOnlyList<string> indexedRoots,
        IReadOnlyList<string> configuredExclusions,
        IReadOnlyList<string> internalExcludedPaths)
    {
        ThrowIfDisposed();
        var replacement = PathFilterSnapshot.Create(indexedRoots, configuredExclusions, internalExcludedPaths);
        _filter = replacement;

        lock (_watcherGate)
        {
            if (!_started)
            {
                return;
            }

            DisposeWatchers();
            CreateAndStartWatchers(replacement.Roots);
        }
    }

    public void PauseUntilBaselineReady()
    {
        ThrowIfDisposed();
        while (true)
        {
            var current = Volatile.Read(ref _baselineReady);
            if (!current.Task.IsCompleted)
            {
                return;
            }

            var replacement = CreateReadySource();
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _baselineReady, replacement, current),
                    current))
            {
                return;
            }
        }
    }

    public void MarkBaselineReady() => Volatile.Read(ref _baselineReady).TrySetResult();

    public void NotifyReconciliationCompleted() => Interlocked.Exchange(ref _reconciliationRequested, 0);

    internal bool TryEnqueueForTests(FileChangeHint hint) => TryEnqueue(hint);

    internal void ReportWatcherErrorForTests(Exception exception) => OnWatcherError(exception);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_watcherGate)
        {
            DisposeWatchers();
        }

        _hints.Writer.TryComplete();
        _cancellation.Cancel();
        _baselineReady.TrySetResult();
        try
        {
            if (!_worker.Wait(DisposeWorkerTimeout))
            {
                Trace.TraceWarning(
                    "Continuous indexing worker did not exit within {0} ms during dispose.",
                    DisposeWorkerTimeout.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(static e => e is OperationCanceledException))
        {
        }
        finally
        {
            _cancellation.Dispose();
        }
    }

    private bool TryEnqueue(FileChangeHint hint)
    {
        if (_disposed || !_filter.ShouldObserve(hint.FullPath, hint.OldFullPath))
        {
            return false;
        }

        if (_hints.Writer.TryWrite(hint))
        {
            return true;
        }

        RequestReconciliationOnce();
        return false;
    }

    private async Task ProcessHintsAsync()
    {
        var cancellationToken = _cancellation.Token;
        try
        {
            while (await _hints.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var pending = new Dictionary<string, PendingPathAction>(StringComparer.OrdinalIgnoreCase);
                DrainAvailableHints(pending);
                if (_debounce > TimeSpan.Zero)
                {
                    await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
                    DrainAvailableHints(pending);
                }

                await Volatile.Read(ref _baselineReady).Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await ApplyPendingAsync(pending, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace.TraceError("Could not apply continuous index changes: {0}", exception);
                    RequestReconciliationOnce();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Continuous indexing worker failed: {0}", exception);
            RequestReconciliationOnce();
        }
    }

    private void DrainAvailableHints(IDictionary<string, PendingPathAction> pending)
    {
        while (_hints.Reader.TryRead(out var hint))
        {
            ReduceHint(pending, hint);
        }
    }

    private void ReduceHint(IDictionary<string, PendingPathAction> pending, FileChangeHint hint)
    {
        var filter = _filter;
        switch (hint.Kind)
        {
            case FileChangeHintKind.Deleted:
                if (filter.ShouldInclude(hint.FullPath))
                {
                    pending[hint.FullPath] = PendingPathAction.Delete;
                }
                break;

            case FileChangeHintKind.Renamed:
                if (!string.IsNullOrWhiteSpace(hint.OldFullPath) && filter.ShouldInclude(hint.OldFullPath))
                {
                    pending[hint.OldFullPath] = PendingPathAction.Delete;
                }

                if (filter.ShouldInclude(hint.FullPath))
                {
                    pending[hint.FullPath] = PendingPathAction.RefreshRecursively;
                }
                break;

            case FileChangeHintKind.Created:
                if (filter.ShouldInclude(hint.FullPath))
                {
                    pending[hint.FullPath] = PendingPathAction.RefreshRecursively;
                }
                break;

            case FileChangeHintKind.Changed:
                if (filter.ShouldInclude(hint.FullPath) &&
                    (!pending.TryGetValue(hint.FullPath, out var existing) || existing != PendingPathAction.RefreshRecursively))
                {
                    pending[hint.FullPath] = PendingPathAction.Refresh;
                }
                break;
        }
    }

    private async Task ApplyPendingAsync(
        IReadOnlyDictionary<string, PendingPathAction> pending,
        CancellationToken cancellationToken)
    {
        var batch = new List<LiveIndexChange>(ApplyBatchSize);
        foreach (var pair in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_filter.ShouldInclude(pair.Key))
            {
                continue;
            }

            if (pair.Value == PendingPathAction.Delete)
            {
                batch.Add(LiveIndexChange.DeletePathAndDescendants(pair.Key));
                await FlushBatchIfFullAsync(batch, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await RefreshPathAsync(
                pair.Key,
                recursive: pair.Value == PendingPathAction.RefreshRecursively,
                batch,
                cancellationToken).ConfigureAwait(false);
        }

        await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshPathAsync(
        string path,
        bool recursive,
        List<LiveIndexChange> batch,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var fileRecord = TryCreateFileRecord(path);
            if (fileRecord is not null)
            {
                batch.Add(LiveIndexChange.Upsert(fileRecord));
                await FlushBatchIfFullAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (!Directory.Exists(path))
        {
            batch.Add(LiveIndexChange.DeletePathAndDescendants(path));
            await FlushBatchIfFullAsync(batch, cancellationToken).ConfigureAwait(false);
            return;
        }

        var directoryRecord = TryCreateDirectoryRecord(path);
        if (directoryRecord is not null)
        {
            batch.Add(LiveIndexChange.Upsert(directoryRecord));
            await FlushBatchIfFullAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        if (!recursive)
        {
            return;
        }

        foreach (var record in EnumerateSubtree(path, cancellationToken))
        {
            if (!_filter.ShouldInclude(record.FullPath))
            {
                continue;
            }

            batch.Add(LiveIndexChange.Upsert(record));
            await FlushBatchIfFullAsync(batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task FlushBatchIfFullAsync(List<LiveIndexChange> batch, CancellationToken cancellationToken) =>
        batch.Count >= ApplyBatchSize
            ? FlushBatchAsync(batch, cancellationToken)
            : Task.CompletedTask;

    private async Task FlushBatchAsync(List<LiveIndexChange> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await _sink.ApplyAsync(batch.ToArray(), cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private IEnumerable<FileRecord> EnumerateSubtree(string rootPath, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(current).EnumerateFileSystemInfos();
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                continue;
            }

            IEnumerator<FileSystemInfo>? enumerator = null;
            try
            {
                enumerator = entries.GetEnumerator();
                while (true)
                {
                    FileSystemInfo entry;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        entry = enumerator.Current;
                    }
                    catch (Exception exception) when (IsExpectedFileSystemException(exception))
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_filter.ShouldInclude(entry.FullName))
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo directory)
                    {
                        FileRecord? record;
                        try
                        {
                            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                continue;
                            }

                            record = FileRecord.Create(
                                directory.FullName,
                                isDirectory: true,
                                sizeBytes: 0,
                                new DateTimeOffset(directory.LastWriteTimeUtc));
                        }
                        catch (Exception exception) when (IsExpectedFileSystemException(exception))
                        {
                            continue;
                        }

                        yield return record;
                        pending.Push(directory.FullName);
                    }
                    else
                    {
                        var record = TryCreateFileRecord(entry.FullName);
                        if (record is not null)
                        {
                            yield return record;
                        }
                    }
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }
    }

    private void CreateAndStartWatchers(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var watcher = _watcherFactory.Create(root, TryEnqueueIgnoringResult, OnWatcherError);
                watcher.Start();
                _watchers.Add(watcher);
            }
            catch (Exception exception) when (IsExpectedFileSystemException(exception))
            {
                Trace.TraceWarning("Could not monitor indexed root '{0}': {1}", root, exception.Message);
                RequestReconciliationOnce();
            }
        }
    }

    private void TryEnqueueIgnoringResult(FileChangeHint hint) => _ = TryEnqueue(hint);

    private void OnWatcherError(Exception exception)
    {
        Trace.TraceWarning("Continuous index watcher lost events: {0}", exception.Message);
        RequestReconciliationOnce();
    }

    private void RequestReconciliationOnce()
    {
        // A full scan (plus its NTFS post-scan journal catch-up) already covers
        // changes observed while the baseline is being built. FileSystemWatcher
        // buffers routinely overflow while millions of rows are scanned; queuing
        // another full scan here makes a busy volume index forever. Hints that fit
        // in the channel remain queued and are applied after the baseline opens.
        if (!Volatile.Read(ref _baselineReady).Task.IsCompleted)
        {
            return;
        }

        if (Interlocked.Exchange(ref _reconciliationRequested, 1) != 0)
        {
            return;
        }

        try
        {
            _requestReconciliation();
        }
        catch (Exception exception)
        {
            Trace.TraceError("Could not request index reconciliation: {0}", exception);
        }
    }

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private static FileRecord? TryCreateFileRecord(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return FileRecord.Create(info.FullName, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return null;
        }
    }

    private static FileRecord? TryCreateDirectoryRecord(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            return FileRecord.Create(info.FullName, true, 0, new DateTimeOffset(info.LastWriteTimeUtc));
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return null;
        }
    }

    private static bool IsExpectedFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;

    private static TaskCompletionSource CreateReadySource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private enum PendingPathAction
    {
        Refresh,
        RefreshRecursively,
        Delete
    }

    private sealed class PathFilterSnapshot
    {
        private readonly string[] _roots;
        private readonly string[] _excludedSubtrees;
        private readonly HashSet<string> _excludedNames;
        private readonly HashSet<string> _excludedExtensions;

        private PathFilterSnapshot(
            string[] roots,
            string[] excludedSubtrees,
            HashSet<string> excludedNames,
            HashSet<string> excludedExtensions)
        {
            _roots = roots;
            _excludedSubtrees = excludedSubtrees;
            _excludedNames = excludedNames;
            _excludedExtensions = excludedExtensions;
        }

        public IReadOnlyList<string> Roots => _roots;

        public static PathFilterSnapshot Create(
            IReadOnlyList<string> roots,
            IReadOnlyList<string> exclusions,
            IReadOnlyList<string> internalExclusions)
        {
            ArgumentNullException.ThrowIfNull(roots);
            ArgumentNullException.ThrowIfNull(exclusions);
            ArgumentNullException.ThrowIfNull(internalExclusions);

            var candidateRoots = roots
                .Select(TryNormalizePath)
                .Where(path => path is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path.Length)
                .ToArray();
            var normalizedRoots = new List<string>(candidateRoots.Length);
            foreach (var candidate in candidateRoots)
            {
                if (!normalizedRoots.Any(root => IsSameOrDescendant(candidate, root)))
                {
                    normalizedRoots.Add(candidate);
                }
            }

            var subtrees = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var configuredValue in exclusions.Concat(internalExclusions))
            {
                if (string.IsNullOrWhiteSpace(configuredValue))
                {
                    continue;
                }

                var expanded = Environment.ExpandEnvironmentVariables(configuredValue.Trim());
                if (expanded.StartsWith("*.", StringComparison.Ordinal))
                {
                    extensions.Add(expanded[1..]);
                }
                else if (Path.IsPathFullyQualified(expanded))
                {
                    var normalized = TryNormalizePath(expanded);
                    if (normalized is not null)
                    {
                        subtrees.Add(normalized);
                    }
                }
                else
                {
                    names.Add(expanded);
                }
            }

            return new PathFilterSnapshot(
                normalizedRoots.ToArray(),
                subtrees.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                names,
                extensions);
        }

        public bool ShouldObserve(string path, string? oldPath) =>
            ShouldInclude(path) || (!string.IsNullOrWhiteSpace(oldPath) && ShouldInclude(oldPath));

        public bool ShouldInclude(string path)
        {
            var normalized = TryNormalizePath(path);
            if (normalized is null || !_roots.Any(root => IsSameOrDescendant(normalized, root)))
            {
                return false;
            }

            if (_excludedSubtrees.Any(excluded => IsSameOrDescendant(normalized, excluded)))
            {
                return false;
            }

            if (_excludedExtensions.Contains(Path.GetExtension(normalized)))
            {
                return false;
            }

            var remaining = normalized[(Path.GetPathRoot(normalized) ?? string.Empty).Length..];
            foreach (var segment in remaining.Split(
                         new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (_excludedNames.Contains(segment))
                {
                    return false;
                }
            }

            return true;
        }

        private static string? TryNormalizePath(string path)
        {
            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or SecurityException)
            {
                return null;
            }
        }

        private static bool IsSameOrDescendant(string path, string root)
        {
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var descendantPrefix = root.EndsWith(Path.DirectorySeparatorChar) ||
                root.EndsWith(Path.AltDirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;
            return path.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
