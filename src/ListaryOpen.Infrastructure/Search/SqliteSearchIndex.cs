using System.Globalization;
using System.IO;
using System.Text;
using System.Diagnostics;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.Infrastructure.Search;

public sealed class SqliteSearchIndex : ISearchIndex, IAsyncDisposable
{
    internal const int CurrentSchemaVersion = 4;
    // Version 5 changes indexed search text to leaf-name aliases and makes the
    // configured fallback root a first-class record. Force one reconciliation
    // so existing checkpoints cannot preserve the old parent-path semantics or
    // omit the indexed root indefinitely.
    internal const long CurrentIndexContentVersion = 5;

    private const int FallbackCandidateLimit = 200;
    private const int CandidateLimitMultiplier = 20;
    private const int MinimumCandidateLimit = 200;
    private const int MaximumCandidateLimit = 5_000;
    private const int UsagePathKeyChunkSize = 500;
    private const int FtsBuildBatchSize = 10_000;
    private const long DefaultIndexGeneration = 0;
    private const string ContentVersionMetadataKey = "index_content_version";
    private const string FilesGenerationMetadataKey = "files_generation";
    private const string LegacyUsageImportMetadataKey = "legacy_usage_imported_from";
    private const string FtsStateMetadataKey = "search_fts_v1_state";
    private const string FtsStateBuilding = "building";
    private const string FtsStateReady = "ready";

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _searchConnectionGate = new(1, 1);
    private readonly object _maintenanceGate = new();
    private readonly SqliteConnection _connection;
    private readonly SqliteConnection _searchConnection;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task _ftsBuildTask = Task.CompletedTask;
    private Task _performanceIndexBuildTask = Task.CompletedTask;
    private volatile bool _ftsReady;
    private volatile bool _preferredRootIndexReady;
    private long _ftsRebuildCount;
    private bool _disposed;
    private bool _bulkIndexing;
    private bool _bulkHasExistingFiles;
    private bool _bulkFtsRebuildRequired;
    private bool _bulkFtsTriggersDropped;

    internal long FtsRebuildCount => Interlocked.Read(ref _ftsRebuildCount);

    private SqliteSearchIndex(
        SqliteConnection connection,
        SqliteConnection searchConnection,
        bool ftsReady,
        bool preferredRootIndexReady)
    {
        _connection = connection;
        _searchConnection = searchConnection;
        _ftsReady = ftsReady;
        _preferredRootIndexReady = preferredRootIndexReady;
    }

    public static async Task<SqliteSearchIndex> OpenAsync(string dbPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("Database path is required.", nameof(dbPath));
        }

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ConfigureWriterConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            // Recover committed frames from an interrupted prior process and
            // shrink a stale WAL before schema work or indexing begins.
            await CheckpointWalCoreAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureSupportedSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await MigrateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await EnsureIndexContentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            var preferredRootIndexReady = await HasIndexAsync(
                connection,
                "ix_files_parent_path_nocase_name",
                cancellationToken).ConfigureAwait(false);
            var ftsReady = await EnsureFtsSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var searchConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();
            var searchConnection = new SqliteConnection(searchConnectionString);
            await searchConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ConfigureSearchConnectionAsync(searchConnection, cancellationToken).ConfigureAwait(false);
                var index = new SqliteSearchIndex(
                    connection,
                    searchConnection,
                    ftsReady,
                    preferredRootIndexReady);
                if (!preferredRootIndexReady)
                {
                    index._performanceIndexBuildTask = Task.Run(
                        () => index.BuildPreferredRootIndexAsync(index._lifetimeCancellation.Token));
                }

                return index;
            }
            catch
            {
                await searchConnection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var command = CreateUpsertCommand(transaction: null);
            await ExecuteUpsertAsync(command, record, DefaultIndexGeneration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task UpsertManyAsync(IEnumerable<FileRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();
            try
            {
                using var backgroundMode = WindowsBackgroundMode.EnterCurrentThread();
                using var command = CreateUpsertCommand(transaction);
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExecuteUpsert(command, record, DefaultIndexGeneration);
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static async Task ConfigureWriterConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            pragma journal_mode = wal;
            pragma synchronous = normal;
            pragma busy_timeout = 5000;
            pragma cache_size = -65536;
            pragma temp_store = memory;
            pragma wal_autocheckpoint = 4096;
            pragma journal_size_limit = 67108864;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConfigureSearchConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            pragma query_only = true;
            pragma busy_timeout = 5000;
            pragma cache_size = -16384;
            pragma mmap_size = 268435456;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<long> BeginIndexingRunAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();
            try
            {
                var currentGeneration = await ReadMetadataInt64Async(
                    _connection,
                    FilesGenerationMetadataKey,
                    transaction,
                    cancellationToken).ConfigureAwait(false) ?? DefaultIndexGeneration;
                var nextGeneration = currentGeneration == long.MaxValue ? 1 : currentGeneration + 1;
                await WriteMetadataAsync(
                    _connection,
                    FilesGenerationMetadataKey,
                    nextGeneration.ToString(CultureInfo.InvariantCulture),
                    transaction,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return nextGeneration;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    internal async Task UpsertManyAsync(
        IEnumerable<FileRecord> records,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (indexGeneration <= DefaultIndexGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(indexGeneration), "Index generation must be positive.");
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();
            try
            {
                using var backgroundMode = WindowsBackgroundMode.EnterCurrentThread();
                using var upsertCommand = CreateUpsertCommand(transaction);
                var pendingUpserts = new List<FileRecord>();
                var reconciledByReference = 0;
                var reconciledByPath = 0;

                if (_bulkIndexing && !_bulkHasExistingFiles)
                {
                    // A new database cannot contain reconciliation hits. Skip the
                    // lookup entirely and sort the bounded insert batch by primary key.
                    foreach (var record in records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pendingUpserts.Add(record);
                    }
                }
                else
                {
                    using var reconcileByReferenceCommand = CreateReconciliationTouchCommand(
                        transaction,
                        useFileReference: true);
                    using var reconcileByPathCommand = CreateReconciliationTouchCommand(
                        transaction,
                        useFileReference: false);
                    // Sync ExecuteNonQuery avoids per-row async state machines on bulk scans.
                    foreach (var record in records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var reconcileCommand = record.FileReferenceNumber > 0
                            ? reconcileByReferenceCommand
                            : reconcileByPathCommand;
                        if (ExecuteUpsert(reconcileCommand, record, indexGeneration) == 0)
                        {
                            pendingUpserts.Add(record);
                        }
                        else if (record.FileReferenceNumber > 0)
                        {
                            reconciledByReference++;
                        }
                        else
                        {
                            reconciledByPath++;
                        }
                    }
                }

                // New and changed rows are uncommon on reconciliation scans. Ordering
                // this bounded remainder keeps primary-key insert/update I/O localized.
                pendingUpserts.Sort(static (left, right) =>
                    string.CompareOrdinal(left.PathKey, right.PathKey));
                foreach (var record in pendingUpserts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ExecuteUpsert(upsertCommand, record, indexGeneration);
                }

                PerformanceMetrics.SetCounter("reconciled_by_file_reference", reconciledByReference);
                PerformanceMetrics.SetCounter("reconciled_by_path", reconciledByPath);
                PerformanceMetrics.SetCounter("full_upserts", pendingUpserts.Count);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Starts a full-scan reconciliation. A brand-new database defers FTS work
    /// until the scan completes; an existing index keeps its triggers so an
    /// unchanged rescan does not rewrite the entire FTS index.
    /// </summary>
    internal async Task BeginBulkIndexingAsync(CancellationToken cancellationToken)
    {
        var backgroundBuild = Volatile.Read(ref _ftsBuildTask);
        if (!backgroundBuild.IsCompleted)
        {
            await backgroundBuild.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_bulkIndexing)
            {
                return;
            }

            var hasExistingFiles = await HasAnyFilesAsync(_connection, cancellationToken).ConfigureAwait(false);
            _bulkHasExistingFiles = hasExistingFiles;
            _bulkFtsTriggersDropped = !hasExistingFiles;
            _bulkFtsRebuildRequired = !_ftsReady || _bulkFtsTriggersDropped;

            if (_bulkFtsTriggersDropped)
            {
                await DropFtsTriggersAsync(_connection, cancellationToken).ConfigureAwait(false);
            }

            if (_bulkFtsRebuildRequired)
            {
                await WriteMetadataAsync(
                        _connection,
                        FtsStateMetadataKey,
                        FtsStateBuilding,
                        transaction: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ftsReady = false;
            }

            _bulkIndexing = true;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Finalizes a full-scan reconciliation, rebuilding FTS only when the prior
    /// snapshot was incomplete or triggers were deferred for a brand-new index.
    /// </summary>
    internal async Task EndBulkIndexingAsync(CancellationToken cancellationToken)
    {
        if (!_bulkIndexing)
        {
            return;
        }

        var shouldCheckpoint = false;
        try
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _bulkIndexing = false;
            _bulkHasExistingFiles = false;
            return;
        }

        try
        {
            if (_disposed || !_bulkIndexing)
            {
                _bulkIndexing = false;
                return;
            }

            if (_bulkFtsTriggersDropped)
            {
                await EnsureFtsTriggersAsync(_connection, cancellationToken).ConfigureAwait(false);
            }

            if (_bulkFtsRebuildRequired)
            {
                await RebuildFtsIndexInBatchesAsync(_connection, cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _ftsRebuildCount);

                await WriteMetadataAsync(
                        _connection,
                        FtsStateMetadataKey,
                        FtsStateReady,
                        transaction: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ftsReady = true;
            }

            shouldCheckpoint = true;
        }
        catch (ObjectDisposedException)
        {
            _bulkIndexing = false;
        }
        finally
        {
            _bulkIndexing = false;
            _bulkHasExistingFiles = false;
            _bulkFtsRebuildRequired = false;
            _bulkFtsTriggersDropped = false;
            try
            {
                _connectionGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (shouldCheckpoint)
        {
            await CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restores incremental FTS maintenance after a canceled full scan without
    /// starting an expensive rebuild during shutdown. The "building" marker is
    /// intentionally retained so the next successful reconciliation can rebuild it.
    /// </summary>
    internal async Task AbortBulkIndexingAsync(CancellationToken cancellationToken)
    {
        if (!_bulkIndexing)
        {
            return;
        }

        try
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            _bulkIndexing = false;
            _bulkHasExistingFiles = false;
            return;
        }

        try
        {
            if (_disposed || !_bulkIndexing)
            {
                _bulkIndexing = false;
                return;
            }

            if (_bulkFtsTriggersDropped)
            {
                await EnsureFtsTriggersAsync(_connection, cancellationToken).ConfigureAwait(false);
            }

            if (_bulkFtsRebuildRequired)
            {
                await WriteMetadataAsync(
                        _connection,
                        FtsStateMetadataKey,
                        FtsStateBuilding,
                        transaction: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                _ftsReady = false;
            }
        }
        catch (ObjectDisposedException)
        {
            _bulkIndexing = false;
        }
        finally
        {
            _bulkIndexing = false;
            _bulkHasExistingFiles = false;
            _bulkFtsRebuildRequired = false;
            _bulkFtsTriggersDropped = false;
            try
            {
                _connectionGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

    }

    internal async Task<int> PruneStaleRecordsUnderRootAsync(
        string rootPath,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (indexGeneration <= DefaultIndexGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(indexGeneration), "Index generation must be positive.");
        }

        var rootPathKey = CreatePathKey(rootPath);
        var descendantPathKeyPattern = EscapeLike(CreateDescendantPathKeyPrefix(rootPathKey)) + "%";

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = """
                delete from files
                where index_generation <> $index_generation
                  and (
                    path_key = $root_path_key
                    or path_key like $descendant_path_key_pattern escape '\'
                  );
                """;
            command.Parameters.AddWithValue("$index_generation", indexGeneration);
            command.Parameters.AddWithValue("$root_path_key", rootPathKey);
            command.Parameters.AddWithValue("$descendant_path_key_pattern", descendantPathKeyPattern);

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task ImportUsageFromAsync(string legacyDbPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyDbPath);

        var fullLegacyDbPath = Path.GetFullPath(legacyDbPath);
        if (!File.Exists(fullLegacyDbPath))
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var importedFrom = await ReadMetadataStringAsync(
                _connection,
                LegacyUsageImportMetadataKey,
                transaction: null,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(importedFrom, fullLegacyDbPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await AttachDatabaseAsync(_connection, fullLegacyDbPath, "legacy", cancellationToken).ConfigureAwait(false);
            try
            {
                if (!await AttachedTableExistsAsync(_connection, "legacy", "usage", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                using var transaction = _connection.BeginTransaction();
                try
                {
                    using (var importCommand = _connection.CreateCommand())
                    {
                        importCommand.Transaction = transaction;
                        importCommand.CommandText = """
                            insert into usage(full_path, path_key, open_count, last_used_at)
                            select full_path, path_key, open_count, last_used_at
                            from legacy.usage
                            where full_path is not null
                              and path_key is not null
                              and open_count >= 0
                              and last_used_at is not null
                            on conflict(path_key) do update set
                                full_path = excluded.full_path,
                                open_count = max(usage.open_count, excluded.open_count),
                                last_used_at = case
                                    when excluded.last_used_at > usage.last_used_at then excluded.last_used_at
                                    else usage.last_used_at
                                end;
                            """;
                        await importCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await WriteMetadataAsync(
                        _connection,
                        LegacyUsageImportMetadataKey,
                        fullLegacyDbPath,
                        transaction,
                        cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                await DetachDatabaseAsync(_connection, "legacy", cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            await ExecuteDeleteAsync(fullPath, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    internal async Task ApplyLiveIndexChangesAsync(
        IReadOnlyList<LiveIndexChange> changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            try
            {
                using var upsertCommand = CreateUpsertCommand(transaction);
                using var deleteTreeCommand = _connection.CreateCommand();
                deleteTreeCommand.Transaction = transaction;
                deleteTreeCommand.CommandText = """
                    delete from files
                    where path_key = $path_key
                       or path_key like $descendant_path_key_pattern escape '\';
                    """;
                var pathKeyParameter = deleteTreeCommand.Parameters.Add("$path_key", SqliteType.Text);
                var descendantsParameter = deleteTreeCommand.Parameters.Add(
                    "$descendant_path_key_pattern",
                    SqliteType.Text);

                foreach (var change in changes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (change.Kind)
                    {
                        case LiveIndexChangeKind.Upsert:
                            await ExecuteUpsertAsync(
                                upsertCommand,
                                change.Record!,
                                DefaultIndexGeneration,
                                cancellationToken).ConfigureAwait(false);
                            break;

                        case LiveIndexChangeKind.DeletePathAndDescendants:
                            var pathKey = CreatePathKey(change.FullPath!);
                            pathKeyParameter.Value = pathKey;
                            descendantsParameter.Value = EscapeLike(CreateDescendantPathKeyPrefix(pathKey)) + "%";
                            await deleteTreeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                            break;

                        default:
                            throw new NotSupportedException($"Unsupported live index change kind: {change.Kind}.");
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task RecordUsageAsync(string fullPath, CancellationToken cancellationToken)
    {
        var normalizedFullPath = NormalizeFullPath(fullPath);
        var pathKey = normalizedFullPath.ToUpperInvariant();

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = """
                insert into usage(full_path, path_key, open_count, last_used_at)
                values ($full_path, $path_key, 1, $last_used_at)
                on conflict(path_key) do update set
                    full_path = excluded.full_path,
                    open_count = usage.open_count + 1,
                    last_used_at = excluded.last_used_at;
                """;
            command.Parameters.AddWithValue("$full_path", normalizedFullPath);
            command.Parameters.AddWithValue("$path_key", pathKey);
            command.Parameters.AddWithValue("$last_used_at", FormatDateTime(DateTimeOffset.UtcNow));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchResult>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        await _searchConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var command = _searchConnection.CreateCommand();
            command.CommandText = """
                select
                    files.full_path,
                    files.is_directory,
                    files.size_bytes,
                    files.last_write_time
                from usage
                inner join files on files.path_key = usage.path_key
                order by usage.last_used_at desc, usage.open_count desc
                limit $limit;
                """;
            command.Parameters.AddWithValue("$limit", Math.Min(limit * 4, 200));

            var results = new List<SearchResult>(limit);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (results.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var record = FileRecord.Create(
                        reader.GetString(0),
                        reader.GetInt64(1) != 0,
                        reader.GetInt64(2),
                        ParseDateTime(reader.GetString(3)));
                    if (record.IsDirectory ? Directory.Exists(record.FullPath) : File.Exists(record.FullPath))
                    {
                        results.Add(new SearchResult(record, 1, "recent"));
                    }
                }
            }

            return results;
        }
        finally
        {
            _searchConnectionGate.Release();
        }
    }

    /// <summary>
    /// Applies USN mutations and persists the volume checkpoint in one transaction.
    /// Prefer <see cref="ApplyUsnMutationsAsync"/> + <see cref="SaveVolumeCheckpointAsync"/> under dual-write.
    /// </summary>
    internal async Task ApplyUsnJournalChangesAsync(
        IEnumerable<UsnJournalIndexChange> changes,
        UsnJournalCheckpoint checkpoint,
        CancellationToken cancellationToken,
        long indexGeneration = DefaultIndexGeneration)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (indexGeneration < DefaultIndexGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(indexGeneration));
        }

        var materializedChanges = changes.ToArray();

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();
            try
            {
                await ApplyUsnMutationsCoreAsync(
                    materializedChanges,
                    indexGeneration,
                    transaction,
                    cancellationToken).ConfigureAwait(false);

                await ExecuteSaveVolumeCheckpointAsync(
                    checkpoint,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// Applies USN file mutations without writing volume checkpoints (KD21).
    /// </summary>
    internal async Task ApplyUsnMutationsAsync(
        IEnumerable<UsnJournalIndexChange> changes,
        CancellationToken cancellationToken,
        long indexGeneration = DefaultIndexGeneration)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (indexGeneration < DefaultIndexGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(indexGeneration));
        }

        var materializedChanges = changes.ToArray();
        if (materializedChanges.Length == 0)
        {
            return;
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var transaction = _connection.BeginTransaction();
            try
            {
                await ApplyUsnMutationsCoreAsync(
                    materializedChanges,
                    indexGeneration,
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ApplyUsnMutationsCoreAsync(
        IReadOnlyList<UsnJournalIndexChange> materializedChanges,
        long indexGeneration,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var upsertCommand = CreateUpsertCommand(transaction);
        using var deleteCommand = CreateDeleteCommand(transaction);
        foreach (var change in materializedChanges)
        {
            switch (change.Kind)
            {
                case UsnJournalIndexChangeKind.Upsert:
                    await ExecuteUpsertAsync(
                        upsertCommand,
                        change.Record!,
                        indexGeneration,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case UsnJournalIndexChangeKind.Delete:
                    await ExecuteDeleteAsync(
                        deleteCommand,
                        change.FullPath!,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case UsnJournalIndexChangeKind.HardLinkResync:
                    await ExecuteHardLinkResyncAsync(
                        upsertCommand,
                        transaction,
                        change.FileReferenceNumber,
                        change.LiveHardLinkRecords ?? Array.Empty<FileRecord>(),
                        indexGeneration,
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new NotSupportedException($"Unsupported USN index change kind: {change.Kind}.");
            }
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<FileRecord> candidates;
        IReadOnlyList<UsageRecord> usage;

        using (PerformanceMetrics.MeasureStage("index.connection_wait"))
        {
            await _searchConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            ThrowIfDisposed();

            using (PerformanceMetrics.MeasureStage("index.read_candidates"))
            {
                candidates = await ReadCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
            }

            using (PerformanceMetrics.MeasureStage("index.read_usage"))
            {
                usage = string.IsNullOrWhiteSpace(query.NormalizedText)
                    ? Array.Empty<UsageRecord>()
                    : await ReadUsageAsync(candidates.Select(record => record.PathKey), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _searchConnectionGate.Release();
        }

        PerformanceMetrics.SetCounter("candidate_count", candidates.Count);
        PerformanceMetrics.SetCounter("usage_count", usage.Count);
        cancellationToken.ThrowIfCancellationRequested();
        using (PerformanceMetrics.MeasureStage("rank.total"))
        {
            return ResultRanker.Rank(query, candidates, usage, Array.Empty<string>(), cancellationToken);
        }
    }

    /// <summary>
    /// Maximum time to wait for FTS/index maintenance before closing connections on dispose.
    /// Exit must not block on a multi-minute FTS rebuild.
    /// </summary>
    internal static readonly TimeSpan DisposeMaintenanceTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Maximum time to wait for connection gates before force-closing on dispose.</summary>
    internal static readonly TimeSpan DisposeGateTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _lifetimeCancellation.Cancel();

        // Background FTS rebuild / preferred-root index can run for a long time and
        // do not always honor cancellation inside SQLite. Bound the wait so app exit
        // stays responsive; connections are closed either way.
        try
        {
            await Task.WhenAll(_ftsBuildTask, _performanceIndexBuildTask)
                .WaitAsync(DisposeMaintenanceTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning("Index background maintenance ended with error during dispose: {0}", exception.Message);
        }

        var searchGateHeld = false;
        var connectionGateHeld = false;
        try
        {
            searchGateHeld = await _searchConnectionGate
                .WaitAsync(DisposeGateTimeout)
                .ConfigureAwait(false);
            if (searchGateHeld)
            {
                connectionGateHeld = await _connectionGate
                    .WaitAsync(DisposeGateTimeout)
                    .ConfigureAwait(false);
            }

            await CloseConnectionsBestEffortAsync(
                    checkpointWal: searchGateHeld && connectionGateHeld)
                .ConfigureAwait(false);
        }
        finally
        {
            if (connectionGateHeld)
            {
                _connectionGate.Release();
            }

            if (searchGateHeld)
            {
                _searchConnectionGate.Release();
            }

            // If a long SQLite rebuild still held a gate, force-close so process exit
            // does not hang waiting for exclusive ownership.
            if (!_disposed)
            {
                await CloseConnectionsBestEffortAsync(checkpointWal: false).ConfigureAwait(false);
            }
        }
    }

    private async Task CloseConnectionsBestEffortAsync(bool checkpointWal)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (checkpointWal)
        {
            try
            {
                await CheckpointWalCoreAsync(_connection, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning("Final WAL checkpoint failed during dispose: {0}", exception.Message);
            }
        }

        try
        {
            await _searchConnection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning("Search connection dispose failed: {0}", exception.Message);
        }

        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning("Write connection dispose failed: {0}", exception.Message);
        }

        try
        {
            _lifetimeCancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            create table if not exists files(
                full_path text not null,
                path_key text not null primary key,
                name text not null,
                parent_path text not null,
                search_text text not null,
                is_directory integer not null check(is_directory in (0, 1)),
                size_bytes integer not null check(size_bytes >= 0),
                last_write_time text not null,
                index_generation integer not null default 0 check(index_generation >= 0),
                file_reference integer not null default 0 check(file_reference >= 0)
            );

            create table if not exists usage(
                full_path text not null,
                path_key text not null primary key,
                open_count integer not null check(open_count >= 0),
                last_used_at text not null
            );

            create table if not exists index_metadata(
                key text not null primary key,
                value text not null
            );

            create table if not exists volume_checkpoints(
                volume_root text not null primary key,
                file_system_name text not null,
                usn_journal_id text not null,
                next_usn text not null,
                rules_version integer not null,
                last_full_scan_at text not null
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Index schema version {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        while (schemaVersion < CurrentSchemaVersion)
        {
            switch (schemaVersion + 1)
            {
                case 1:
                    await MigrateToSchemaVersion1Async(connection, cancellationToken).ConfigureAwait(false);
                    break;
                case 2:
                    await RemoveObsoleteIndexesAsync(connection, cancellationToken).ConfigureAwait(false);
                    break;
                case 3:
                    await MigrateToSchemaVersion3Async(connection, cancellationToken).ConfigureAwait(false);
                    break;
                case 4:
                    await MigrateToSchemaVersion4Async(connection, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Missing index migration for schema version {schemaVersion + 1}.");
            }

            schemaVersion++;
            await WriteSchemaVersionAsync(connection, schemaVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureSupportedSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var schemaVersion = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Index schema version {schemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }
    }

    private static async Task MigrateToSchemaVersion1Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(connection, "files", "search_text", cancellationToken).ConfigureAwait(false))
        {
            using var addColumnCommand = connection.CreateCommand();
            addColumnCommand.CommandText = "alter table files add column search_text text not null default '';";
            await addColumnCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await HasColumnAsync(connection, "files", "index_generation", cancellationToken).ConfigureAwait(false))
        {
            using var addColumnCommand = connection.CreateCommand();
            addColumnCommand.CommandText = "alter table files add column index_generation integer not null default 0;";
            await addColumnCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureIndexContentVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        await BackfillSearchTextAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureBaseIndexesAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "pragma user_version;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task WriteSchemaVersionAsync(
        SqliteConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"pragma user_version = {version};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EnsureFtsSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                create virtual table if not exists files_fts_v1 using fts5(
                    name,
                    parent_path,
                    search_text,
                    tokenize = 'trigram',
                    content = 'files',
                    content_rowid = 'rowid'
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureFtsTriggersAsync(connection, cancellationToken).ConfigureAwait(false);

        var state = await ReadMetadataStringAsync(
            connection,
            FtsStateMetadataKey,
            transaction: null,
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(state, FtsStateReady, StringComparison.Ordinal))
        {
            return true;
        }

        if (!await HasAnyFilesAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            await WriteMetadataAsync(
                connection,
                FtsStateMetadataKey,
                FtsStateReady,
                transaction: null,
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        await WriteMetadataAsync(
            connection,
            FtsStateMetadataKey,
            FtsStateBuilding,
            transaction: null,
            cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task BuildFtsIndexAsync(CancellationToken cancellationToken)
    {
        var rebuilt = false;
        try
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RebuildFtsIndexInBatchesAsync(_connection, cancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Increment(ref _ftsRebuildCount);

                await WriteMetadataAsync(
                    _connection,
                    FtsStateMetadataKey,
                    FtsStateReady,
                    transaction: null,
                    cancellationToken).ConfigureAwait(false);
                _ftsReady = true;
                rebuilt = true;
            }
            finally
            {
                _connectionGate.Release();
            }

            if (rebuilt)
            {
                await CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("FTS index build failed: {0}", exception);
        }
    }

    private async Task BuildPreferredRootIndexAsync(CancellationToken cancellationToken)
    {
        var built = false;
        try
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    create index if not exists ix_files_parent_path_nocase_name
                    on files(parent_path collate nocase, name collate nocase);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _preferredRootIndexReady = true;
                built = true;
            }
            finally
            {
                _connectionGate.Release();
            }

            if (built)
            {
                await CheckpointWalAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError("Preferred-root index build failed: {0}", exception);
        }
    }

    /// <summary>
    /// Rebuilds the external-content FTS table in bounded transactions. FTS5's
    /// built-in 'rebuild' command is one transaction and can grow the WAL to the
    /// size of the entire index before SQLite gets an opportunity to checkpoint.
    /// </summary>
    private static async Task RebuildFtsIndexInBatchesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (WindowsBackgroundMode.EnterCurrentThread())
        using (var clearCommand = connection.CreateCommand())
        {
            clearCommand.CommandText = "insert into files_fts_v1(files_fts_v1) values('delete-all');";
            clearCommand.ExecuteNonQuery();
        }

        long lastRowId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long? batchLastRowId;
            using (WindowsBackgroundMode.EnterCurrentThread())
            using (var boundaryCommand = connection.CreateCommand())
            {
                boundaryCommand.CommandText = """
                    select max(rowid)
                    from (
                        select rowid
                        from files
                        where rowid > $last_rowid
                        order by rowid
                        limit $limit
                    );
                    """;
                boundaryCommand.Parameters.AddWithValue("$last_rowid", lastRowId);
                boundaryCommand.Parameters.AddWithValue("$limit", FtsBuildBatchSize);
                var value = boundaryCommand.ExecuteScalar();
                batchLastRowId = value is null or DBNull
                    ? null
                    : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }

            if (batchLastRowId is null)
            {
                return;
            }

            using (WindowsBackgroundMode.EnterCurrentThread())
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    using var insertCommand = connection.CreateCommand();
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = """
                        insert into files_fts_v1(rowid, name, parent_path, search_text)
                        select rowid, name, parent_path, search_text
                        from files
                        where rowid > $last_rowid
                          and rowid <= $batch_last_rowid
                        order by rowid;
                        """;
                    insertCommand.Parameters.AddWithValue("$last_rowid", lastRowId);
                    insertCommand.Parameters.AddWithValue("$batch_last_rowid", batchLastRowId.Value);
                    insertCommand.ExecuteNonQuery();
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }

            lastRowId = batchLastRowId.Value;
            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckpointWalAsync(CancellationToken cancellationToken)
    {
        // All code that needs both gates uses search-then-writer order, avoiding
        // a dispose/checkpoint deadlock with an in-flight interactive query.
        await _searchConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await CheckpointWalCoreAsync(_connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        finally
        {
            _searchConnectionGate.Release();
        }
    }

    private static async Task CheckpointWalCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "pragma wal_checkpoint(truncate);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var busy = reader.GetInt32(0);
        var logFrames = reader.GetInt32(1);
        var checkpointedFrames = reader.GetInt32(2);
        if (busy != 0)
        {
            Trace.TraceWarning(
                "WAL truncate checkpoint remained busy (log frames: {0}, checkpointed: {1}).",
                logFrames,
                checkpointedFrames);
        }
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from pragma_table_info($table_name)
            where name = $column_name;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        command.Parameters.AddWithValue("$column_name", columnName);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> HasIndexAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            select count(*)
            from sqlite_master
            where type = 'index' and name = $index_name;
            """;
        command.Parameters.AddWithValue("$index_name", indexName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    internal async Task EnsureFtsReadyAsync(CancellationToken cancellationToken)
    {
        var buildTask = GetOrStartFtsBuild();
        await buildTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task WaitForBackgroundMaintenanceAsync() =>
        Task.WhenAll(GetOrStartFtsBuild(), _performanceIndexBuildTask);

    private Task GetOrStartFtsBuild()
    {
        lock (_maintenanceGate)
        {
            if (_ftsReady || _disposed)
            {
                return Task.CompletedTask;
            }

            if (_ftsBuildTask.IsCompleted)
            {
                // Recovery is lazy: the app starts a reconciliation immediately
                // after opening the database. Starting a multi-gigabyte rebuild here
                // would race that scan and then make it rebuild FTS a second time.
                _ftsBuildTask = Task.Run(
                    () => BuildFtsIndexAsync(_lifetimeCancellation.Token));
            }

            return _ftsBuildTask;
        }
    }

    private static async Task EnsureIndexContentVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var contentVersion = await ReadMetadataInt64Async(
            connection,
            ContentVersionMetadataKey,
            transaction: null,
            cancellationToken).ConfigureAwait(false);
        if (contentVersion == CurrentIndexContentVersion)
        {
            return;
        }

        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = """
                        drop trigger if exists files_fts_v1_insert;
                        drop trigger if exists files_fts_v1_delete;
                        drop trigger if exists files_fts_v1_update;
                        drop table if exists files_fts_v1;
                        delete from files;
                        delete from volume_checkpoints;
                        """;
                    await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await WriteMetadataAsync(
                    connection,
                    ContentVersionMetadataKey,
                    CurrentIndexContentVersion.ToString(CultureInfo.InvariantCulture),
                    transaction,
                    cancellationToken).ConfigureAwait(false);
                await WriteMetadataAsync(
                    connection,
                    FilesGenerationMetadataKey,
                    DefaultIndexGeneration.ToString(CultureInfo.InvariantCulture),
                    transaction,
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task<bool> HasAnyFilesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select exists(select 1 from files limit 1);";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<long?> ReadMetadataInt64Async(
        SqliteConnection connection,
        string key,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var value = await ReadMetadataStringAsync(connection, key, transaction, cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static async Task<string?> ReadMetadataStringAsync(
        SqliteConnection connection,
        string key,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select value
            from index_metadata
            where key = $key;
            """;
        command.Parameters.AddWithValue("$key", key);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task WriteMetadataAsync(
        SqliteConnection connection,
        string key,
        string value,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into index_metadata(key, value)
            values ($key, $value)
            on conflict(key) do update set value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task SaveVolumeCheckpointAsync(
        UsnJournalCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            await ExecuteSaveVolumeCheckpointAsync(checkpoint, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    internal async Task<UsnJournalCheckpoint?> ReadVolumeCheckpointAsync(
        string volumeRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeRoot);

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = """
                select
                    volume_root,
                    file_system_name,
                    usn_journal_id,
                    next_usn,
                    rules_version,
                    last_full_scan_at
                from volume_checkpoints
                where volume_root = $volume_root;
                """;
            command.Parameters.AddWithValue("$volume_root", volumeRoot);

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return new UsnJournalCheckpoint(
                    reader.GetString(0),
                    reader.GetString(1),
                    ulong.Parse(reader.GetString(2), NumberStyles.Integer, CultureInfo.InvariantCulture),
                    long.Parse(reader.GetString(3), NumberStyles.Integer, CultureInfo.InvariantCulture),
                    reader.GetInt64(4),
                    ParseDateTime(reader.GetString(5)));
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static async Task AttachDatabaseAsync(
        SqliteConnection connection,
        string dbPath,
        string alias,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"attach database $db_path as {alias};";
        command.Parameters.AddWithValue("$db_path", dbPath);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DetachDatabaseAsync(
        SqliteConnection connection,
        string alias,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"detach database {alias};";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> AttachedTableExistsAsync(
        SqliteConnection connection,
        string alias,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            select count(*)
            from {alias}.sqlite_master
            where type = 'table'
              and name = $table_name;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task BackfillSearchTextAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<(string PathKey, string FullPath)>();

        using (var readCommand = connection.CreateCommand())
        {
            readCommand.CommandText = """
                select
                    path_key,
                    full_path
                from files
                where search_text = '';
                """;

            var reader = await readCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                update files
                set search_text = $search_text
                where path_key = $path_key;
                """;
            updateCommand.Parameters.Add("$search_text", SqliteType.Text);
            updateCommand.Parameters.Add("$path_key", SqliteType.Text);
            updateCommand.Prepare();

            foreach (var row in rows)
            {
                updateCommand.Parameters[0].Value = CreateRecordSearchText(row.FullPath);
                updateCommand.Parameters[1].Value = row.PathKey;

                await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task EnsureBaseIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            create index if not exists ix_files_name on files(name);
            create index if not exists ix_files_name_nocase on files(name collate nocase);
            create index if not exists ix_files_is_directory_name on files(is_directory, name);
            create index if not exists ix_files_is_directory_name_nocase on files(is_directory, name collate nocase);
            create index if not exists ix_usage_path_key on usage(path_key);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteCommand CreateUpsertCommand(SqliteTransaction? transaction)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into files (
                full_path,
                path_key,
                name,
                parent_path,
                search_text,
                is_directory,
                size_bytes,
                last_write_time,
                index_generation,
                file_reference
            )
            values (
                $full_path,
                $path_key,
                $name,
                $parent_path,
                $search_text,
                $is_directory,
                $size_bytes,
                $last_write_time,
                $index_generation,
                $file_reference
            )
            on conflict(path_key) do update set
                full_path = excluded.full_path,
                name = excluded.name,
                parent_path = excluded.parent_path,
                search_text = excluded.search_text,
                is_directory = excluded.is_directory,
                size_bytes = excluded.size_bytes,
                last_write_time = excluded.last_write_time,
                index_generation = case
                    when excluded.index_generation > 0 then excluded.index_generation
                    else files.index_generation
                end,
                file_reference = case
                    when excluded.file_reference > 0 then excluded.file_reference
                    else files.file_reference
                end
            where files.full_path is not excluded.full_path
               or files.name is not excluded.name
               or files.parent_path is not excluded.parent_path
               or files.search_text is not excluded.search_text
               or files.is_directory is not excluded.is_directory
               or files.size_bytes is not excluded.size_bytes
               or files.last_write_time is not excluded.last_write_time
               or (excluded.file_reference > 0 and files.file_reference is not excluded.file_reference);
            """;

        command.Parameters.Add("$full_path", SqliteType.Text);
        command.Parameters.Add("$path_key", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$parent_path", SqliteType.Text);
        command.Parameters.Add("$search_text", SqliteType.Text);
        command.Parameters.Add("$is_directory", SqliteType.Integer);
        command.Parameters.Add("$size_bytes", SqliteType.Integer);
        command.Parameters.Add("$last_write_time", SqliteType.Text);
        command.Parameters.Add("$index_generation", SqliteType.Integer);
        command.Parameters.Add("$file_reference", SqliteType.Integer);
        command.Prepare();
        return command;
    }

    /// <summary>
    /// Handles an unchanged full-rescan row with one narrow update. NTFS records use
    /// the compact file-reference index in the scanner's natural MFT order; records
    /// without an identity fall back to the path primary key. A zero result means the
    /// row is new or changed, so the regular upsert handles the uncommon path.
    /// </summary>
    private SqliteCommand CreateReconciliationTouchCommand(
        SqliteTransaction transaction,
        bool useFileReference)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        var lookup = useFileReference
            ? "files indexed by ix_files_file_reference"
            : "files";
        var referenceFilter = useFileReference
            ? "and file_reference > 0 and file_reference = $file_reference"
            : string.Empty;
        command.CommandText = $"""
            update {lookup}
            set index_generation = $index_generation,
                file_reference = case
                    when $file_reference > 0 then $file_reference
                    else file_reference
                end
            where path_key = $path_key
              {referenceFilter}
              and full_path is $full_path
              and name is $name
              and parent_path is $parent_path
              and search_text is $search_text
              and is_directory is $is_directory
              and size_bytes is $size_bytes
              and last_write_time is $last_write_time;
            """;
        AddUpsertParameters(command);
        command.Prepare();
        return command;
    }

    private static void AddUpsertParameters(SqliteCommand command)
    {
        command.Parameters.Add("$full_path", SqliteType.Text);
        command.Parameters.Add("$path_key", SqliteType.Text);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$parent_path", SqliteType.Text);
        command.Parameters.Add("$search_text", SqliteType.Text);
        command.Parameters.Add("$is_directory", SqliteType.Integer);
        command.Parameters.Add("$size_bytes", SqliteType.Integer);
        command.Parameters.Add("$last_write_time", SqliteType.Text);
        command.Parameters.Add("$index_generation", SqliteType.Integer);
        command.Parameters.Add("$file_reference", SqliteType.Integer);
    }

    private static async Task ExecuteUpsertAsync(
        SqliteCommand command,
        FileRecord record,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteUpsert(command, record, indexGeneration);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static int ExecuteUpsert(
        SqliteCommand command,
        FileRecord record,
        long indexGeneration)
    {
        ArgumentNullException.ThrowIfNull(record);

        command.Parameters[0].Value = record.FullPath;
        command.Parameters[1].Value = record.PathKey;
        command.Parameters[2].Value = record.Name;
        command.Parameters[3].Value = record.ParentPath;
        command.Parameters[4].Value = CreateRecordSearchText(record.FullPath);
        command.Parameters[5].Value = record.IsDirectory ? 1 : 0;
        command.Parameters[6].Value = record.SizeBytes;
        command.Parameters[7].Value = FormatDateTime(record.LastWriteTime);
        command.Parameters[8].Value = indexGeneration;
        command.Parameters[9].Value = unchecked((long)record.FileReferenceNumber);
        return command.ExecuteNonQuery();
    }

    private static async Task DropFtsTriggersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            drop trigger if exists files_fts_v1_insert;
            drop trigger if exists files_fts_v1_delete;
            drop trigger if exists files_fts_v1_update;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureFtsTriggersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            create trigger if not exists files_fts_v1_insert after insert on files begin
                insert into files_fts_v1(rowid, name, parent_path, search_text)
                values (new.rowid, new.name, new.parent_path, new.search_text);
            end;

            create trigger if not exists files_fts_v1_delete after delete on files begin
                insert into files_fts_v1(files_fts_v1, rowid, name, parent_path, search_text)
                values ('delete', old.rowid, old.name, old.parent_path, old.search_text);
            end;

            create trigger if not exists files_fts_v1_update
            after update of name, parent_path, search_text on files
            when old.name <> new.name
              or old.parent_path <> new.parent_path
              or old.search_text <> new.search_text
            begin
                insert into files_fts_v1(files_fts_v1, rowid, name, parent_path, search_text)
                values ('delete', old.rowid, old.name, old.parent_path, old.search_text);
                insert into files_fts_v1(rowid, name, parent_path, search_text)
                values (new.rowid, new.name, new.parent_path, new.search_text);
            end;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToSchemaVersion3Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(connection, "files", "file_reference", cancellationToken).ConfigureAwait(false))
        {
            using var addColumn = connection.CreateCommand();
            addColumn.CommandText =
                "alter table files add column file_reference integer not null default 0 check(file_reference >= 0);";
            await addColumn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            create index if not exists ix_files_file_reference
                on files(file_reference)
                where file_reference > 0;
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateToSchemaVersion4Async(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Only paths that need transliteration have search_text. A partial covering
        // index lets 1-2 character pinyin-initial searches scan that small subset
        // instead of every file row and its much wider table payload.
        using var indexCommand = connection.CreateCommand();
        indexCommand.CommandText = """
            create index if not exists ix_files_search_text_nonempty
                on files(search_text)
                where search_text <> '';
            """;
        await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteHardLinkResyncAsync(
        SqliteCommand upsertCommand,
        SqliteTransaction transaction,
        ulong fileReferenceNumber,
        IReadOnlyList<FileRecord> liveRecords,
        long indexGeneration,
        CancellationToken cancellationToken)
    {
        if (fileReferenceNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fileReferenceNumber));
        }

        // Upsert live hard-link names stamped with this FRN, then purge any other
        // indexed path that already carries the same file_reference. FRN=0 legacy
        // rows (pre-stamp USN upserts) are removed by companion Delete(usnPath)
        // changes emitted by the elevated journal producer when the USN name is
        // not in the live set.
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in liveRecords)
        {
            var stamped = record.WithFileReferenceNumber(fileReferenceNumber);
            liveKeys.Add(stamped.PathKey);
            await ExecuteUpsertAsync(upsertCommand, stamped, indexGeneration, cancellationToken)
                .ConfigureAwait(false);
        }

        var volumeRoots = liveRecords
            .Select(record => Path.GetPathRoot(record.FullPath))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root!.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        using var selectCommand = transaction.Connection!.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = """
            select path_key, full_path
            from files
            where file_reference = $file_reference;
            """;
        selectCommand.Parameters.AddWithValue("$file_reference", unchecked((long)fileReferenceNumber));

        var stalePaths = new List<string>();
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var pathKey = reader.GetString(0);
                var fullPath = reader.GetString(1);
                if (liveKeys.Contains(pathKey))
                {
                    continue;
                }

                // FRNs are volume-scoped. When live records identify volume roots,
                // only purge stale names under those roots (empty live set => purge all FRN matches).
                if (volumeRoots.Length > 0)
                {
                    var root = Path.GetPathRoot(fullPath)?.ToUpperInvariant() ?? string.Empty;
                    if (!volumeRoots.Contains(root, StringComparer.Ordinal))
                    {
                        continue;
                    }
                }

                stalePaths.Add(fullPath);
            }
        }

        using var deleteCommand = transaction.Connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "delete from files where path_key = $path_key;";
        deleteCommand.Parameters.Add("$path_key", SqliteType.Text);
        deleteCommand.Prepare();
        foreach (var path in stalePaths)
        {
            await ExecuteDeleteAsync(deleteCommand, path, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RemoveObsoleteIndexesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "drop index if exists ix_files_search_text;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteDeleteAsync(
        string fullPath,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateDeleteCommand(transaction);
        await ExecuteDeleteAsync(command, fullPath, cancellationToken).ConfigureAwait(false);
    }

    private SqliteCommand CreateDeleteCommand(SqliteTransaction? transaction)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from files where path_key = $path_key;";
        command.Parameters.Add("$path_key", SqliteType.Text);
        command.Prepare();
        return command;
    }

    private static async Task ExecuteDeleteAsync(
        SqliteCommand command,
        string fullPath,
        CancellationToken cancellationToken)
    {
        command.Parameters[0].Value = CreatePathKey(fullPath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteSaveVolumeCheckpointAsync(
        UsnJournalCheckpoint checkpoint,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into volume_checkpoints(
                volume_root,
                file_system_name,
                usn_journal_id,
                next_usn,
                rules_version,
                last_full_scan_at
            )
            values(
                $volume_root,
                $file_system_name,
                $usn_journal_id,
                $next_usn,
                $rules_version,
                $last_full_scan_at
            )
            on conflict(volume_root) do update set
                file_system_name = excluded.file_system_name,
                usn_journal_id = excluded.usn_journal_id,
                next_usn = excluded.next_usn,
                rules_version = excluded.rules_version,
                last_full_scan_at = excluded.last_full_scan_at;
            """;
        command.Parameters.AddWithValue("$volume_root", checkpoint.VolumeRoot);
        command.Parameters.AddWithValue("$file_system_name", checkpoint.FileSystemName);
        command.Parameters.AddWithValue("$usn_journal_id", checkpoint.UsnJournalId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$next_usn", checkpoint.NextUsn.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rules_version", checkpoint.RulesVersion);
        command.Parameters.AddWithValue("$last_full_scan_at", FormatDateTime(checkpoint.LastFullScanAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FileRecord>> ReadCandidatesAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query.NormalizedText))
        {
            using (PerformanceMetrics.MeasureStage("candidates.fallback"))
            {
                await AddFallbackCandidatesAsync(query, query.Limit, records, cancellationToken).ConfigureAwait(false);
            }

            return records.Values
                .Where(record => MatchesParsedFilters(query, record))
                .ToArray();
        }

        var candidateLimit = CreateCandidateLimit(query);
        var useExpensiveFuzzy = UsesExpensiveFuzzyCandidates(query);

        if (query.PreferredRoot is not null && _preferredRootIndexReady)
        {
            using (PerformanceMetrics.MeasureStage("candidates.preferred_root"))
            {
                await AddPreferredRootCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
            }
        }

        using (PerformanceMetrics.MeasureStage("candidates.exact"))
        {
            await AddExactCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
        }

        // Short queries (1-2 chars): always pull alias candidates even if exact prefix already
        // filled the display limit (ASCII ht* names must not starve 合同 / initials "ht").
        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldUseShortAliasCandidates(query))
        {
            using (PerformanceMetrics.MeasureStage("candidates.short_alias"))
            {
                await AddShortAliasCandidatesAsync(query, candidateLimit, records, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Short pinyin (1-2 chars) always keeps FTS as a recall backstop. Longer plain
        // tokens that already hit the name-prefix index (including CamelCase→snake_case
        // variants) skip trigram FTS: on multi-GB snapshots a rare-phrase MATCH is often
        // 50–200ms and ResultRanker + preferred-root already cover interactive ranking.
        if (useExpensiveFuzzy
            && (ShouldUseShortAliasCandidates(query)
                || (records.Count < query.Limit && !HasUsefulNamePrefixHits(query, records.Count))))
        {
            using (PerformanceMetrics.MeasureStage("candidates.fts"))
            {
                await AddFuzzyCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (records.Count < query.Limit)
        {
            using (PerformanceMetrics.MeasureStage("candidates.fallback"))
            {
                await AddFallbackCandidatesAsync(query, FallbackCandidateLimit, records, cancellationToken).ConfigureAwait(false);
            }
        }

        using (PerformanceMetrics.MeasureStage("candidates.filter"))
        {
            return records.Values
                .Where(record => MatchesParsedFilters(query, record))
                .ToArray();
        }
    }

    internal static bool UsesExpensiveFuzzyCandidatesForTests(SearchQuery query)
        => UsesExpensiveFuzzyCandidates(query);

    internal static bool ShouldUseShortAliasCandidatesForTests(SearchQuery query)
        => ShouldUseShortAliasCandidates(query);

    /// <summary>
    /// FTS/fuzzy candidate expansion is enabled from length 2 so short pinyin initials
    /// (e.g. "ht" for 合同) can retrieve rows; length-1 stays exact/short-alias only.
    /// </summary>
    private static bool UsesExpensiveFuzzyCandidates(SearchQuery query)
        => NormalizeSearchText(query.NormalizedText).Length >= 2;

    private static bool ShouldUseShortAliasCandidates(SearchQuery query)
    {
        var length = NormalizeSearchText(query.NormalizedText).Length;
        return length is >= 1 and <= 2;
    }

    /// <summary>
    /// True when the name-prefix pass already found hits via CamelCase→snake_case (or
    /// kebab-case) expansion so trigram FTS can be skipped. Plain lowercase tokens still
    /// use FTS for mid-string recall (e.g. "doc" → mydoc.txt).
    /// </summary>
    private static bool HasUsefulNamePrefixHits(SearchQuery query, int recordCount)
    {
        if (recordCount <= 0)
        {
            return false;
        }

        // Multi-term / operator queries still need FTS for path and phrase recall.
        if (query.Parsed.Terms.Count + query.Parsed.Phrases.Count != 1
            || query.Parsed.PathTerms.Count > 0
            || query.Parsed.ExcludedTerms.Count > 0)
        {
            return false;
        }

        // Only treat prefix hits as "enough" when casing-driven variants were available
        // (ListaryOpen → listary_open). All-lowercase queries keep FTS mid-string recall.
        return CreateNamePrefixVariants(query).Count > 1;
    }

    private async Task AddExactCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var directoryFilter = CreateDirectoryFilter(query, "files.");
        foreach (var prefix in CreateNamePrefixVariants(query))
        {
            if (records.Count >= candidateLimit)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var remaining = candidateLimit - records.Count;
            using var command = _searchConnection.CreateCommand();
            var parsedFilter = CreateParsedFilter(query, command, "files.");
            command.CommandText = $"""
                select
                    full_path,
                    is_directory,
                    size_bytes,
                    last_write_time
                from files
                where name >= $query collate nocase
                  and name < $query_upper_bound collate nocase
                  {directoryFilter}{parsedFilter}
                order by
                    case
                        when name = $query then 0
                        when name >= $query collate nocase
                         and name < $query_upper_bound collate nocase then 1
                        else 2
                    end,
                    length(name),
                    name,
                    length(full_path),
                    full_path
                limit $limit;
                """;
            command.Parameters.AddWithValue("$query", prefix);
            command.Parameters.AddWithValue("$query_upper_bound", prefix + '\uFFFF');
            command.Parameters.AddWithValue("$limit", remaining);

            await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Name-prefix variants for the indexed range scan. PascalCase/camelCase tokens
    /// also probe snake_case and kebab-case so "ListaryOpen" hits listary_open* via the
    /// name index instead of a multi-hundred-ms trigram FTS intersection.
    /// </summary>
    internal static IReadOnlyList<string> CreateNamePrefixVariantsForTests(SearchQuery query)
        => CreateNamePrefixVariants(query);

    private static IReadOnlyList<string> CreateNamePrefixVariants(SearchQuery query)
    {
        var normalized = NormalizeSearchText(query.NormalizedText);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var variants = new List<string>(3) { normalized };
        if (normalized.Length < 3
            || normalized.Contains(' ', StringComparison.Ordinal)
            || query.Parsed.Terms.Count + query.Parsed.Phrases.Count != 1)
        {
            return variants;
        }

        // Boundary detection needs the user's original casing; ranking text is lowercased.
        var originalToken = query.Text.Trim();
        var operatorIndex = originalToken.IndexOf(':');
        if (operatorIndex >= 0)
        {
            return variants;
        }

        var snake = CamelOrPascalToSnakeCase(originalToken);
        if (snake.Length >= 3
            && !string.Equals(snake, normalized, StringComparison.OrdinalIgnoreCase))
        {
            variants.Add(snake);
            var kebab = snake.Replace('_', '-');
            if (!string.Equals(kebab, snake, StringComparison.Ordinal))
            {
                variants.Add(kebab);
            }
        }

        return variants;
    }

    private static string CamelOrPascalToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                var previous = value[index - 1];
                var nextIsLower = index + 1 < value.Length && char.IsLower(value[index + 1]);
                if (char.IsLower(previous) || (char.IsUpper(previous) && nextIsLower))
                {
                    builder.Append('_');
                }
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private async Task AddFuzzyCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        // Keep querying the last committed FTS snapshot while a bulk scan rebuilds it.
        // Bulk indexing deliberately drops the maintenance triggers, but the previous
        // snapshot remains readable and is substantially more useful than disabling
        // substring/pinyin recall for the entire (potentially long-running) scan.
        // ResultRanker validates every returned row against current file data, so stale
        // terms can never surface as false matches.
        var termsQuery = CreateFtsMatchQuery(query);
        if (string.IsNullOrWhiteSpace(termsQuery))
        {
            return;
        }

        // One all-column MATCH avoids paying trigram intersection twice (name, then
        // parent_path/search_text) on large FTS snapshots. ResultRanker reorders hits.
        var ftsLimit = records.Count == 0
            ? candidateLimit
            : Math.Clamp(query.Limit * 2, MinimumCandidateLimit, candidateLimit);
        await AddFtsCandidatesAsync(
            query,
            termsQuery,
            ftsLimit,
            records,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFtsCandidatesAsync(
        SearchQuery query,
        string matchQuery,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {

        var directoryFilter = CreateDirectoryFilter(query, "files.");

        using var command = _searchConnection.CreateCommand();
        var parsedFilter = CreateParsedFilter(query, command, "files.");
        // No ORDER BY on non-FTS columns: that forced a full match materialization before
        // LIMIT. ResultRanker applies the interactive ranking order.
        command.CommandText = $"""
            select
                files.full_path,
                files.is_directory,
                files.size_bytes,
                files.last_write_time
            from files_fts_v1
            inner join files on files.rowid = files_fts_v1.rowid
            where files_fts_v1 match $match{directoryFilter}{parsedFilter}
            limit $limit;
            """;
        command.Parameters.AddWithValue("$match", matchQuery);
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFallbackCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        using var command = _searchConnection.CreateCommand();
        var whereClause = CreateFallbackWhereClause(query, command);
        command.CommandText = $"""
            select
                full_path,
                is_directory,
                size_bytes,
                last_write_time
            from files
            {whereClause}
            order by
                name,
                full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddPreferredRootCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var preferredRoot = query.PreferredRoot;
        if (preferredRoot is null)
        {
            return;
        }

        await AddPreferredRootDirectChildrenAsync(
            query,
            preferredRoot,
            candidateLimit,
            records,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AddPreferredRootDirectChildrenAsync(
        SearchQuery query,
        string preferredRoot,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchText(query.NormalizedText);
        var containsPattern = $"%{EscapeLike(normalizedQuery)}%";
        var orderedPattern = CreateOrderedLikePattern(normalizedQuery);
        var directoryFilter = CreateDirectoryFilter(query, "files.");

        using var command = _searchConnection.CreateCommand();
        var parsedFilter = CreateParsedFilter(query, command, "files.");
        command.CommandText = $"""
            select
                files.full_path,
                files.is_directory,
                files.size_bytes,
                files.last_write_time
            from files
            where files.parent_path = $root collate nocase
              and (
                lower(files.name) like $contains escape '\'
                or files.search_text like $contains escape '\'
                or lower(files.name) like $ordered escape '\'
                or files.search_text like $ordered escape '\'
              ){directoryFilter}{parsedFilter}
            order by
                length(files.name),
                files.name collate nocase,
                files.name,
                files.full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$root", preferredRoot);
        command.Parameters.AddWithValue("$contains", containsPattern);
        command.Parameters.AddWithValue("$ordered", orderedPattern);
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private static string CreateFtsMatchQuery(SearchQuery query)
    {
        // Plain terms search the leaf name (plus name-derived transliteration
        // aliases), never parent_path. Path operators and quoted phrases retain
        // their explicit full-path semantics through parsed filtering.
        var clauses = new List<string>();
        foreach (var term in query.Parsed.Terms.Select(NormalizeSearchText).Where(term => term.Length >= 2))
        {
            var escaped = $"\"{term.Replace("\"", "\"\"")}\"";
            clauses.Add($"(name : {escaped} OR search_text : {escaped})");
        }

        foreach (var phrase in query.Parsed.Phrases.Select(NormalizeSearchText).Where(term => term.Length >= 2))
        {
            clauses.Add($"\"{phrase.Replace("\"", "\"\"")}\"");
        }

        return clauses.Count == 0 ? string.Empty : string.Join(" AND ", clauses);
    }

    private async Task AddShortAliasCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchText(query.NormalizedText);
        if (normalizedQuery.Length is < 1 or > 2)
        {
            return;
        }

        var directoryFilter = CreateDirectoryFilter(query, "files.");
        using var command = _searchConnection.CreateCommand();
        var parsedFilter = CreateParsedFilter(query, command, "files.");
        // Match precomputed pinyin/initials in search_text (space-separated aliases).
        // ASCII names are already covered by the indexed name-prefix query above;
        // keeping them out of this branch avoids a full-table lower(name) scan.
        command.CommandText = $"""
            select
                files.full_path,
                files.is_directory,
                files.size_bytes,
                files.last_write_time
            from files indexed by ix_files_search_text_nonempty
            where files.search_text <> ''
              and files.search_text like $alias_contains escape '\'
              {directoryFilter}{parsedFilter}
            order by
                length(files.name),
                files.name collate nocase,
                files.name,
                length(files.full_path),
                files.full_path
            limit $limit;
            """;
        var escaped = EscapeLike(normalizedQuery);
        // Substring match covers initials embedded in tokens (e.g. "ht.docx") and standalone aliases.
        command.Parameters.AddWithValue("$alias_contains", "%" + escaped + "%");
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesParsedFilters(SearchQuery query, FileRecord record)
    {
        if (query.IsDirectory is not null && record.IsDirectory != query.IsDirectory)
        {
            return false;
        }

        if (query.RequiredExtensions.Count > 0 &&
            (record.IsDirectory || !query.RequiredExtensions.Contains(GetExtension(record), StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (query.ModifiedAfter is not null && record.LastWriteTime < query.ModifiedAfter)
        {
            return false;
        }

        if (query.Parsed.FileOnly && record.IsDirectory)
        {
            return false;
        }

        if (query.EffectiveMode == SearchMode.FoldersOnly && !record.IsDirectory)
        {
            return false;
        }

        if (query.Parsed.Extensions.Count > 0 &&
            !ExtensionMatchesAny(record.Name, query.Parsed.Extensions))
        {
            return false;
        }

        if (ExtensionMatchesAny(record.Name, query.Parsed.ExcludedExtensions))
        {
            return false;
        }

        foreach (var term in query.Parsed.PathTerms)
        {
            if (!PathSegmentMatcher.Matches(record.FullPath, term))
            {
                return false;
            }
        }

        foreach (var phrase in query.Parsed.Phrases)
        {
            if (!ContainsIgnoreCase(record.FullPath, phrase))
            {
                return false;
            }
        }

        foreach (var term in query.Parsed.ExcludedTerms)
        {
            if (ContainsIgnoreCase(record.FullPath, term))
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateFallbackWhereClause(SearchQuery query, SqliteCommand command)
    {
        var clauses = new List<string>();
        if (query.IsDirectory is not null)
        {
            clauses.Add($"is_directory = {(query.IsDirectory.Value ? 1 : 0)}");
        }
        else if (query.Parsed.FileOnly)
        {
            clauses.Add("is_directory = 0");
        }
        else if (query.EffectiveMode == SearchMode.FoldersOnly)
        {
            clauses.Add("is_directory = 1");
        }

        var parsedFilter = CreateParsedFilter(query, command);
        if (!string.IsNullOrWhiteSpace(parsedFilter))
        {
            clauses.Add(parsedFilter[" and ".Length..]);
        }

        return clauses.Count == 0 ? string.Empty : "where " + string.Join(" and ", clauses);
    }

    private static string CreateParsedFilter(
        SearchQuery query,
        SqliteCommand command,
        string tablePrefix = "")
    {
        var clauses = new List<string>();
        if (query.RequiredExtensions.Count > 0)
        {
            var extensionClauses = new List<string>();
            for (var index = 0; index < query.RequiredExtensions.Count; index++)
            {
                var parameterName = $"$quick_ext_{index}";
                command.Parameters.AddWithValue(parameterName, $"%.{EscapeLike(query.RequiredExtensions[index])}");
                extensionClauses.Add($"lower({tablePrefix}name) like {parameterName} escape '\\'");
            }

            clauses.Add("(" + string.Join(" or ", extensionClauses) + ")");
        }

        if (query.ModifiedAfter is not null)
        {
            command.Parameters.AddWithValue("$quick_modified_after", FormatDateTime(query.ModifiedAfter.Value));
            clauses.Add($"julianday({tablePrefix}last_write_time) >= julianday($quick_modified_after)");
        }

        // Include extensions are OR'd (any match). Exclude extensions are AND'd (must match none).
        AddExtensionFilters(clauses, command, query.Parsed.Extensions, $"lower({tablePrefix}name)", "include_ext", include: true);
        AddExtensionFilters(clauses, command, query.Parsed.ExcludedExtensions, $"lower({tablePrefix}name)", "exclude_ext", include: false);
        AddSearchTextFilters(clauses, command, query.Parsed.PathTerms, tablePrefix, "path", include: true);
        AddSearchTextFilters(clauses, command, query.Parsed.Phrases, tablePrefix, "phrase", include: true);
        AddSearchTextFilters(clauses, command, query.Parsed.ExcludedTerms, tablePrefix, "exclude", include: false);

        return clauses.Count == 0 ? string.Empty : " and " + string.Join(" and ", clauses);
    }

    /// <summary>
    /// Include extensions: (name like %.pdf OR name like %.docx).
    /// Exclude extensions: name not like %.tmp AND name not like %.bak.
    /// </summary>
    private static void AddExtensionFilters(
        ICollection<string> clauses,
        SqliteCommand command,
        IReadOnlyList<string> values,
        string column,
        string namePrefix,
        bool include)
    {
        var extensionClauses = new List<string>();
        for (var index = 0; index < values.Count; index++)
        {
            var value = NormalizeSearchText(values[index]);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parameterName = $"${namePrefix}_{index}";
            var pattern = $"%.{EscapeLike(value)}";
            extensionClauses.Add(include
                ? $"{column} like {parameterName} escape '\\'"
                : $"{column} not like {parameterName} escape '\\'");
            command.Parameters.AddWithValue(parameterName, pattern);
        }

        if (extensionClauses.Count == 0)
        {
            return;
        }

        if (include)
        {
            clauses.Add("(" + string.Join(" or ", extensionClauses) + ")");
        }
        else
        {
            foreach (var clause in extensionClauses)
            {
                clauses.Add(clause);
            }
        }
    }

    private static void AddSearchTextFilters(
        ICollection<string> clauses,
        SqliteCommand command,
        IReadOnlyList<string> values,
        string tablePrefix,
        string namePrefix,
        bool include)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = NormalizeSearchText(values[index]);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Multi-segment path: path:foo\bar → each segment must appear (SQL AND of LIKEs).
            // Ordered segment checks happen in MatchesParsedFilters via PathSegmentMatcher.
            if (include && namePrefix == "path" && value.IndexOfAny(['\\', '/']) >= 0)
            {
                var segments = PathSegmentMatcher.SplitSegments(value);
                if (segments.Count > 1)
                {
                    var segmentClauses = new List<string>();
                    for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                    {
                        var parameterName = $"${namePrefix}_{index}_{segmentIndex}";
                        var pattern = $"%{EscapeLike(segments[segmentIndex])}%";
                        segmentClauses.Add(
                            $"(lower({tablePrefix}full_path) like {parameterName} escape '\\' or {tablePrefix}search_text like {parameterName} escape '\\')");
                        command.Parameters.AddWithValue(parameterName, pattern);
                    }

                    clauses.Add("(" + string.Join(" and ", segmentClauses) + ")");
                    continue;
                }
            }

            var singleParameterName = $"${namePrefix}_{index}";
            var singlePattern = $"%{EscapeLike(value)}%";
            clauses.Add(include
                ? $"(lower({tablePrefix}full_path) like {singleParameterName} escape '\\' or {tablePrefix}search_text like {singleParameterName} escape '\\')"
                : $"(lower({tablePrefix}full_path) not like {singleParameterName} escape '\\' and {tablePrefix}search_text not like {singleParameterName} escape '\\')");
            command.Parameters.AddWithValue(singleParameterName, singlePattern);
        }
    }

    private static string GetExtension(FileRecord record)
    {
        return Path.GetExtension(record.Name).TrimStart('.');
    }

    /// <summary>
    /// Aligns with SQL LIKE '%.ext' intent, including compound extensions (tar.gz).
    /// </summary>
    private static bool ExtensionMatchesAny(string fileName, IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0 || string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            if (fileName.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string value, string term)
    {
        return value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AddRecordsAsync(
        SqliteCommand command,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var record = FileRecord.Create(
                    reader.GetString(0),
                    reader.GetInt64(1) != 0,
                    reader.GetInt64(2),
                    ParseDateTime(reader.GetString(3)));
                records.TryAdd(record.PathKey, record);
            }
        }
    }

    private async Task<IReadOnlyList<UsageRecord>> ReadUsageAsync(
        IEnumerable<string> pathKeys,
        CancellationToken cancellationToken)
    {
        var uniquePathKeys = pathKeys
            .Where(pathKey => !string.IsNullOrEmpty(pathKey))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (uniquePathKeys.Length == 0)
        {
            return Array.Empty<UsageRecord>();
        }

        var records = new List<UsageRecord>();
        for (var offset = 0; offset < uniquePathKeys.Length; offset += UsagePathKeyChunkSize)
        {
            var count = Math.Min(UsagePathKeyChunkSize, uniquePathKeys.Length - offset);
            var parameterNames = new string[count];

            using var command = _searchConnection.CreateCommand();
            for (var i = 0; i < count; i++)
            {
                var parameterName = $"$path_key_{i}";
                parameterNames[i] = parameterName;
                command.Parameters.AddWithValue(parameterName, uniquePathKeys[offset + i]);
            }

            command.CommandText = $"""
                select
                    full_path,
                    open_count,
                    last_used_at
                from usage
                where path_key in ({string.Join(", ", parameterNames)});
                """;

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(new UsageRecord(
                        reader.GetString(0),
                        reader.GetInt32(1),
                        ParseDateTime(reader.GetString(2))));
                }
            }
        }

        return records;
    }

    private static int CreateCandidateLimit(SearchQuery query)
    {
        return Math.Clamp(query.Limit * CandidateLimitMultiplier, MinimumCandidateLimit, MaximumCandidateLimit);
    }

    private static string CreateDirectoryFilter(SearchQuery query, string tablePrefix = "")
    {
        if (query.IsDirectory is not null)
        {
            return $" and {tablePrefix}is_directory = {(query.IsDirectory.Value ? 1 : 0)}";
        }

        if (query.Parsed.FileOnly)
        {
            return $" and {tablePrefix}is_directory = 0";
        }

        return query.EffectiveMode == SearchMode.FoldersOnly ? $" and {tablePrefix}is_directory = 1" : string.Empty;
    }

    private static string CreatePathKey(string fullPath)
        => NormalizeFullPath(fullPath).ToUpperInvariant();

    private static string NormalizeFullPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            throw new ArgumentException("Path is required.", nameof(fullPath));
        }

        var trimmed = fullPath.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
        {
            throw new ArgumentException("Path must be fully qualified.", nameof(fullPath));
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Path is invalid.", nameof(fullPath), ex);
        }
    }

    private static string CreateDescendantPathKeyPrefix(string rootPathKey)
    {
        if (rootPathKey.EndsWith(Path.DirectorySeparatorChar)
            || rootPathKey.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return rootPathKey;
        }

        return rootPathKey + Path.DirectorySeparatorChar;
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDateTime(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string CreateRecordSearchText(string fullPath)
        => PinyinMatcher.CreateSearchAliases(Path.GetFileName(fullPath));

    private static string NormalizeSearchText(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string CreateOrderedLikePattern(string value)
    {
        var builder = new StringBuilder("%");
        foreach (var ch in value)
        {
            AppendEscapedLikeCharacter(builder, ch);
            builder.Append('%');
        }

        return builder.ToString();
    }

    private static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            AppendEscapedLikeCharacter(builder, ch);
        }

        return builder.ToString();
    }

    private static void AppendEscapedLikeCharacter(StringBuilder builder, char value)
    {
        if (value is '%' or '_' or '\\')
        {
            builder.Append('\\');
        }

        builder.Append(value);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
