using System.Globalization;
using System.IO;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.Infrastructure.Search;

public sealed class SqliteSearchIndex : ISearchIndex, IAsyncDisposable
{
    private const int FallbackCandidateLimit = 200;
    private const int CandidateLimitMultiplier = 20;
    private const int MinimumCandidateLimit = 200;
    private const int MaximumCandidateLimit = 5_000;
    private const int UsagePathKeyChunkSize = 500;

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private SqliteSearchIndex(SqliteConnection connection)
    {
        _connection = connection;
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
            await CreateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await MigrateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            return new SqliteSearchIndex(connection);
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
            await ExecuteUpsertAsync(record, null, cancellationToken).ConfigureAwait(false);
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
                    await ExecuteUpsertAsync(record, transaction, cancellationToken).ConfigureAwait(false);
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

    public async Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
    {
        var pathKey = CreatePathKey(fullPath);

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            using var command = _connection.CreateCommand();
            command.CommandText = "delete from files where path_key = $path_key;";
            command.Parameters.AddWithValue("$path_key", pathKey);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var candidates = await ReadCandidatesAsync(query, cancellationToken).ConfigureAwait(false);
            var usage = await ReadUsageAsync(candidates.Select(record => record.PathKey), cancellationToken).ConfigureAwait(false);

            return ResultRanker.Rank(query, candidates, usage, Array.Empty<string>());
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectionGate.Release();
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
                last_write_time text not null
            );

            create table if not exists usage(
                full_path text not null,
                path_key text not null primary key,
                open_count integer not null check(open_count >= 0),
                last_used_at text not null
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

        await BackfillSearchTextAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureIndexesAsync(connection, cancellationToken).ConfigureAwait(false);
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
            create index if not exists ix_files_is_directory_name on files(is_directory, name);
            create index if not exists ix_files_search_text on files(search_text);
            create index if not exists ix_usage_path_key on usage(path_key);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteUpsertAsync(
        FileRecord record,
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
                last_write_time
            )
            values (
                $full_path,
                $path_key,
                $name,
                $parent_path,
                $search_text,
                $is_directory,
                $size_bytes,
                $last_write_time
            )
            on conflict(path_key) do update set
                full_path = excluded.full_path,
                name = excluded.name,
                parent_path = excluded.parent_path,
                search_text = excluded.search_text,
                is_directory = excluded.is_directory,
                size_bytes = excluded.size_bytes,
                last_write_time = excluded.last_write_time;
            """;

        command.Parameters.AddWithValue("$full_path", record.FullPath);
        command.Parameters.AddWithValue("$path_key", record.PathKey);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$parent_path", record.ParentPath);
        command.Parameters.AddWithValue("$search_text", CreateRecordSearchText(record.FullPath, record.Name));
        command.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$last_write_time", FormatDateTime(record.LastWriteTime));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<FileRecord>> ReadCandidatesAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, FileRecord>(StringComparer.Ordinal);
        var candidateLimit = CreateCandidateLimit(query);

        await AddExactCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
        await AddFuzzyCandidatesAsync(query, candidateLimit, records, cancellationToken).ConfigureAwait(false);
        await AddFallbackCandidatesAsync(query, records, cancellationToken).ConfigureAwait(false);

        return records.Values.ToArray();
    }

    private async Task AddExactCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchText(query.NormalizedText);
        var containsPattern = $"%{EscapeLike(normalizedQuery)}%";
        var directoryFilter = CreateDirectoryFilter(query);

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            select
                full_path,
                is_directory,
                size_bytes,
                last_write_time
            from files
            where search_text like $contains escape '\'{directoryFilter}
            order by
                case
                    when name = $query then 0
                    when name like $query_prefix escape '\' then 1
                    else 2
                end,
                length(name),
                name,
                length(full_path),
                full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$contains", containsPattern);
        command.Parameters.AddWithValue("$query", normalizedQuery);
        command.Parameters.AddWithValue("$query_prefix", $"{EscapeLike(normalizedQuery)}%");
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFuzzyCandidatesAsync(
        SearchQuery query,
        int candidateLimit,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchText(query.NormalizedText);
        var orderedPattern = CreateOrderedLikePattern(normalizedQuery);
        var directoryFilter = CreateDirectoryFilter(query);

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            select
                full_path,
                is_directory,
                size_bytes,
                last_write_time
            from files
            where search_text like $ordered escape '\'{directoryFilter}
            order by
                case
                    when name like $query_prefix escape '\' then 0
                    when name like $first_character_prefix escape '\' then 1
                    else 2
                end,
                case
                    when instr(lower(name), $first_character) > 0 then instr(lower(name), $first_character)
                    else 2147483647
                end,
                length(name),
                name,
                length(full_path),
                full_path
            limit $limit;
            """;
        command.Parameters.AddWithValue("$ordered", orderedPattern);
        command.Parameters.AddWithValue("$query_prefix", $"{EscapeLike(normalizedQuery)}%");
        command.Parameters.AddWithValue("$first_character_prefix", $"{EscapeLike(normalizedQuery[0].ToString())}%");
        command.Parameters.AddWithValue("$first_character", normalizedQuery[0].ToString());
        command.Parameters.AddWithValue("$limit", candidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddFallbackCandidatesAsync(
        SearchQuery query,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var whereClause = query.Mode == SearchMode.FoldersOnly
            ? "where is_directory = 1"
            : string.Empty;

        using var command = _connection.CreateCommand();
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
        command.Parameters.AddWithValue("$limit", FallbackCandidateLimit);

        await AddRecordsAsync(command, records, cancellationToken).ConfigureAwait(false);
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

            using var command = _connection.CreateCommand();
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

    private static string CreateDirectoryFilter(SearchQuery query)
    {
        return query.Mode == SearchMode.FoldersOnly ? " and is_directory = 1" : string.Empty;
    }

    private static string CreatePathKey(string fullPath)
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
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            return normalized.ToUpperInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Path is invalid.", nameof(fullPath), ex);
        }
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
