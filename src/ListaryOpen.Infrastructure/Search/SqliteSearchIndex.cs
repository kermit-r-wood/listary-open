using System.Globalization;
using System.IO;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.Infrastructure.Search;

public sealed class SqliteSearchIndex : ISearchIndex, IAsyncDisposable
{
    internal const long CurrentIndexContentVersion = 2;

    private const int FallbackCandidateLimit = 200;
    private const int CandidateLimitMultiplier = 20;
    private const int MinimumCandidateLimit = 200;
    private const int MaximumCandidateLimit = 5_000;
    private const int UsagePathKeyChunkSize = 500;
    private const long DefaultIndexGeneration = 0;
    private const string ContentVersionMetadataKey = "index_content_version";
    private const string FilesGenerationMetadataKey = "files_generation";
    private const string LegacyUsageImportMetadataKey = "legacy_usage_imported_from";
    private const string FtsStateMetadataKey = "search_fts_v1_state";
    private const string FtsStateBuilding = "building";
    private const string FtsStateReady = "ready";

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _searchConnectionGate = new(1, 1);
    private readonly SqliteConnection _connection;
    private readonly SqliteConnection _searchConnection;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task _ftsBuildTask = Task.CompletedTask;
    private volatile bool _ftsReady;
    private bool _disposed;

    private SqliteSearchIndex(
        SqliteConnection connection,
        SqliteConnection searchConnection,
        bool ftsReady)
    {
        _connection = connection;
        _searchConnection = searchConnection;
        _ftsReady = ftsReady;
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
            RegisterSearchFunctions(connection);
            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await MigrateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
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
                RegisterSearchFunctions(searchConnection);
                var index = new SqliteSearchIndex(connection, searchConnection, ftsReady);
                if (!ftsReady)
                {
                    index._ftsBuildTask = index.BuildFtsIndexAsync(index._lifetimeCancellation.Token);
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
            await ExecuteUpsertAsync(record, DefaultIndexGeneration, null, cancellationToken).ConfigureAwait(false);
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
                foreach (var record in records)
                {
                    await ExecuteUpsertAsync(record, DefaultIndexGeneration, transaction, cancellationToken).ConfigureAwait(false);
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

    private static async Task ConfigureWriterConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            pragma journal_mode = wal;
            pragma synchronous = normal;
            pragma busy_timeout = 5000;
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
                foreach (var record in records)
                {
                    await ExecuteUpsertAsync(record, indexGeneration, transaction, cancellationToken).ConfigureAwait(false);
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
                foreach (var change in materializedChanges)
                {
                    switch (change.Kind)
                    {
                        case UsnJournalIndexChangeKind.Upsert:
                            await ExecuteUpsertAsync(
                                change.Record!,
                                indexGeneration,
                                transaction,
                                cancellationToken).ConfigureAwait(false);
                            break;

                        case UsnJournalIndexChangeKind.Delete:
                            await ExecuteDeleteAsync(
                                change.FullPath!,
                                transaction,
                                cancellationToken).ConfigureAwait(false);
                            break;

                        default:
                            throw new NotSupportedException($"Unsupported USN index change kind: {change.Kind}.");
                    }
                }

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

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<FileRecord> candidates;
        IReadOnlyList<UsageRecord> usage;

        await _searchConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            candidates = await ReadCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
            usage = string.IsNullOrWhiteSpace(query.NormalizedText)
                ? Array.Empty<UsageRecord>()
                : await ReadUsageAsync(candidates.Select(record => record.PathKey), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _searchConnectionGate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ResultRanker.Rank(query, candidates, usage, Array.Empty<string>());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        await _ftsBuildTask.ConfigureAwait(false);
        await _searchConnectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _connectionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await _searchConnection.DisposeAsync().ConfigureAwait(false);
                await _connection.DisposeAsync().ConfigureAwait(false);
                _lifetimeCancellation.Dispose();
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
                index_generation integer not null default 0 check(index_generation >= 0)
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
        await EnsureIndexesAsync(connection, cancellationToken).ConfigureAwait(false);
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

        var state = await ReadMetadataStringAsync(
            connection,
            FtsStateMetadataKey,
            transaction: null,
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(state, FtsStateReady, StringComparison.Ordinal))
        {
            return true;
        }

        if (await CountFilesAsync(connection, cancellationToken).ConfigureAwait(false) == 0)
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
        try
        {
            await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using (var buildCommand = _connection.CreateCommand())
                {
                    buildCommand.CommandText = "insert into files_fts_v1(files_fts_v1) values('rebuild');";
                    await buildCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await WriteMetadataAsync(
                    _connection,
                    FtsStateMetadataKey,
                    FtsStateReady,
                    transaction: null,
                    cancellationToken).ConfigureAwait(false);
                _ftsReady = true;

                using var checkpointCommand = _connection.CreateCommand();
                checkpointCommand.CommandText = "pragma wal_checkpoint(truncate);";
                await checkpointCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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

        var existingFileCount = await CountFilesAsync(connection, cancellationToken).ConfigureAwait(false);

        using (var transaction = connection.BeginTransaction())
        {
            try
            {
                using (var deleteCommand = connection.CreateCommand())
                {
                    deleteCommand.Transaction = transaction;
                    deleteCommand.CommandText = "delete from files;";
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

        if (existingFileCount > 0)
        {
            await VacuumAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> CountFilesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from files;";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
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

    private static async Task VacuumAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "vacuum;";

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BackfillSearchTextAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<(string PathKey, string FullPath, string Name)>();

        using (var readCommand = connection.CreateCommand())
        {
            readCommand.CommandText = """
                select
                    path_key,
                    full_path,
                    name
                from files
                where search_text = '';
                """;

            var reader = await readCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
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
            foreach (var row in rows)
            {
                using var updateCommand = connection.CreateCommand();
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    update files
                    set search_text = $search_text
                    where path_key = $path_key;
                    """;
                updateCommand.Parameters.AddWithValue("$search_text", CreateRecordSearchText(row.FullPath, row.Name));
                updateCommand.Parameters.AddWithValue("$path_key", row.PathKey);

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

    private static async Task EnsureIndexesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            create index if not exists ix_files_name on files(name);
            create index if not exists ix_files_name_nocase on files(name collate nocase);
            create index if not exists ix_files_is_directory_name on files(is_directory, name);
            create index if not exists ix_files_is_directory_name_nocase on files(is_directory, name collate nocase);
            create index if not exists ix_files_search_text on files(search_text);
            create index if not exists ix_usage_path_key on usage(path_key);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteUpsertAsync(
        FileRecord record,
        long indexGeneration,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var command = _connection.CreateCommand();
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
                index_generation
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
                $index_generation
            )
            on conflict(path_key) do update set
                full_path = excluded.full_path,
                name = excluded.name,
                parent_path = excluded.parent_path,
                search_text = excluded.search_text,
                is_directory = excluded.is_directory,
                size_bytes = excluded.size_bytes,
                last_write_time = excluded.last_write_time,
                index_generation = excluded.index_generation;
            """;

        command.Parameters.AddWithValue("$full_path", record.FullPath);
        command.Parameters.AddWithValue("$path_key", record.PathKey);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$parent_path", record.ParentPath);
        command.Parameters.AddWithValue("$search_text", CreateRecordSearchText(record.FullPath, record.Name));
        command.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$last_write_time", FormatDateTime(record.LastWriteTime));
        command.Parameters.AddWithValue("$index_generation", indexGeneration);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteDeleteAsync(
        string fullPath,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var pathKey = CreatePathKey(fullPath);

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "delete from files where path_key = $path_key;";
        command.Parameters.AddWithValue("$path_key", pathKey);

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
            await AddFallbackCandidatesAsync(query, query.Limit, records, cancellationToken).ConfigureAwait(false);
            return records.Values
                .Where(record => MatchesParsedFilters(query, record))
                .ToArray();
        }

        var candidateLimit = CreateCandidateLimit(query);
        var useExpensiveFuzzy = UsesExpensiveFuzzyCandidates(query);

        await AddExactCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (useExpensiveFuzzy)
        {
            await AddFuzzyCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await AddFallbackCandidatesAsync(query, FallbackCandidateLimit, records, cancellationToken).ConfigureAwait(false);

        return records.Values
            .Where(record => MatchesParsedFilters(query, record))
            .ToArray();
    }

    internal static bool UsesExpensiveFuzzyCandidatesForTests(SearchQuery query)
        => UsesExpensiveFuzzyCandidates(query);

    private static bool UsesExpensiveFuzzyCandidates(SearchQuery query)
        => NormalizeSearchText(query.NormalizedText).Length >= 3;

    private async Task AddExactCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchText(query.NormalizedText);
        var directoryFilter = CreateDirectoryFilter(query, "files.");

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
        command.Parameters.AddWithValue("$query", normalizedQuery);
        command.Parameters.AddWithValue("$query_upper_bound", normalizedQuery + '\uFFFF');
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFuzzyCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        if (!_ftsReady)
        {
            return;
        }

        var matchQuery = CreateFtsMatchQuery(query);
        if (string.IsNullOrWhiteSpace(matchQuery))
        {
            return;
        }

        var directoryFilter = CreateDirectoryFilter(query, "files.");

        using var command = _searchConnection.CreateCommand();
        var parsedFilter = CreateParsedFilter(query, command, "files.");
        command.CommandText = $"""
            select
                files.full_path,
                files.is_directory,
                files.size_bytes,
                files.last_write_time
            from files_fts_v1
            inner join files on files.rowid = files_fts_v1.rowid
            where files_fts_v1 match $match{directoryFilter}{parsedFilter}
            order by
                length(files.name),
                files.name collate nocase,
                files.name,
                length(files.full_path),
                files.full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$match", matchQuery);
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddUsageCandidatesAsync(
        SearchQuery query,
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
            inner join usage on usage.path_key = files.path_key
            where (
                files.search_text like $contains escape '\'
                or files.search_text like $ordered escape '\'
            ){directoryFilter}{parsedFilter}
            order by
                min(usage.open_count * 5, 50) desc,
                usage.last_used_at desc,
                files.name,
                files.full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$contains", containsPattern);
        command.Parameters.AddWithValue("$ordered", orderedPattern);
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddCombinedScoreCandidatesAsync(
        SearchQuery query,
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
            left join usage on usage.path_key = files.path_key
            where (
                files.search_text like $contains escape '\'
                or files.search_text like $ordered escape '\'
            ){directoryFilter}{parsedFilter}
            order by
                (
                    listary_rank_score($query, files.full_path, files.name)
                    + min(coalesce(usage.open_count, 0) * 5, 50)
                    + listary_recency_boost(usage.last_used_at, $now)
                ) desc,
                files.name collate nocase,
                files.name,
                files.full_path collate nocase,
                files.full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$contains", containsPattern);
        command.Parameters.AddWithValue("$ordered", orderedPattern);
        command.Parameters.AddWithValue("$query", query.NormalizedText);
        command.Parameters.AddWithValue("$now", FormatDateTime(DateTimeOffset.UtcNow));
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

    private static string CreateFtsMatchQuery(SearchQuery query)
    {
        var terms = query.Parsed.Phrases
            .Concat(query.Parsed.Terms)
            .Select(NormalizeSearchText)
            .Where(term => term.Length >= 3)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"")
            .ToArray();
        return terms.Length == 0 ? string.Empty : string.Join(" AND ", terms);
    }

    private static bool MatchesParsedFilters(SearchQuery query, FileRecord record)
    {
        if (query.Parsed.FileOnly && record.IsDirectory)
        {
            return false;
        }

        if (query.EffectiveMode == SearchMode.FoldersOnly && !record.IsDirectory)
        {
            return false;
        }

        if (query.Parsed.Extensions.Count > 0 &&
            !query.Parsed.Extensions.Contains(GetExtension(record), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Parsed.ExcludedExtensions.Contains(GetExtension(record), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var term in query.Parsed.PathTerms)
        {
            if (!ContainsIgnoreCase(record.FullPath, term))
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
        if (query.Parsed.FileOnly)
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
        AddLikeFilters(clauses, command, query.Parsed.Extensions, $"lower({tablePrefix}name)", "include_ext", include: true, extension: true);
        AddLikeFilters(clauses, command, query.Parsed.ExcludedExtensions, $"lower({tablePrefix}name)", "exclude_ext", include: false, extension: true);
        AddLikeFilters(clauses, command, query.Parsed.PathTerms, $"{tablePrefix}search_text", "path", include: true, extension: false);
        AddLikeFilters(clauses, command, query.Parsed.Phrases, $"{tablePrefix}search_text", "phrase", include: true, extension: false);
        AddLikeFilters(clauses, command, query.Parsed.ExcludedTerms, $"{tablePrefix}search_text", "exclude", include: false, extension: false);

        return clauses.Count == 0 ? string.Empty : " and " + string.Join(" and ", clauses);
    }

    private static void AddLikeFilters(
        ICollection<string> clauses,
        SqliteCommand command,
        IReadOnlyList<string> values,
        string column,
        string namePrefix,
        bool include,
        bool extension)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var value = NormalizeSearchText(values[index]);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parameterName = $"${namePrefix}_{index}";
            var pattern = extension
                ? $"%.{EscapeLike(value)}"
                : $"%{EscapeLike(value)}%";
            clauses.Add($"{column} {(include ? string.Empty : "not ")}like {parameterName} escape '\\'");
            command.Parameters.AddWithValue(parameterName, pattern);
        }
    }

    private static string GetExtension(FileRecord record)
    {
        return Path.GetExtension(record.Name).TrimStart('.');
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
        if (query.Parsed.FileOnly)
        {
            return $" and {tablePrefix}is_directory = 0";
        }

        return query.EffectiveMode == SearchMode.FoldersOnly ? $" and {tablePrefix}is_directory = 1" : string.Empty;
    }

    private static void RegisterSearchFunctions(SqliteConnection connection)
    {
        connection.CreateFunction<string, string, string, double>(
            "listary_rank_score",
            CalculateSqlRankScore,
            isDeterministic: true);
        connection.CreateFunction<string?, string, double>(
            "listary_recency_boost",
            CalculateSqlRecencyBoost,
            isDeterministic: false);
    }

    private static double CalculateSqlRankScore(string query, string fullPath, string name)
    {
        var nameScore = FuzzyMatcher.Score(query, name);
        var pathScore = FuzzyMatcher.Score(query, fullPath) * 0.6;
        var pinyinScore = PinyinMatcher.Score(query, name) * 0.9;

        return Math.Max(nameScore, Math.Max(pathScore, pinyinScore));
    }

    private static double CalculateSqlRecencyBoost(string? lastUsedAt, string now)
    {
        if (string.IsNullOrWhiteSpace(lastUsedAt))
        {
            return 0;
        }

        if (!DateTimeOffset.TryParse(lastUsedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedLastUsedAt) ||
            !DateTimeOffset.TryParse(now, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedNow))
        {
            return 0;
        }

        var daysSinceUse = (parsedNow - parsedLastUsedAt).TotalDays;
        return Math.Clamp(20 - daysSinceUse, 0, 20);
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

    private static string CreateRecordSearchText(string fullPath, string name)
    {
        return string.Join(
            ' ',
            PinyinMatcher.CreateSearchText(fullPath),
            PinyinMatcher.CreateSearchText(name));
    }

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
