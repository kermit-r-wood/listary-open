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
    private const int QueryCandidateLimit = 5000;
    private const int FallbackCandidateLimit = 200;

    private readonly SqliteConnection _connection;

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
        await connection.OpenAsync(cancellationToken);

        try
        {
            await CreateSchemaAsync(connection, cancellationToken);
            return new SqliteSearchIndex(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task UpsertAsync(FileRecord record, CancellationToken cancellationToken)
    {
        await ExecuteUpsertAsync(record, null, cancellationToken);
    }

    public async Task UpsertManyAsync(IEnumerable<FileRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        using var transaction = _connection.BeginTransaction();
        try
        {
            foreach (var record in records)
            {
                await ExecuteUpsertAsync(record, transaction, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
    {
        var pathKey = CreatePathKey(fullPath);

        using var command = _connection.CreateCommand();
        command.CommandText = "delete from files where path_key = $path_key;";
        command.Parameters.AddWithValue("$path_key", pathKey);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await ReadCandidatesAsync(query, cancellationToken);
        var usage = await ReadUsageAsync(cancellationToken);

        return ResultRanker.Rank(query, candidates, usage, Array.Empty<string>());
    }

    public ValueTask DisposeAsync()
    {
        return _connection.DisposeAsync();
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

        await command.ExecuteNonQueryAsync(cancellationToken);
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
        command.Parameters.AddWithValue("$search_text", CreateRecordSearchText(record));
        command.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$last_write_time", FormatDateTime(record.LastWriteTime));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<FileRecord>> ReadCandidatesAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, FileRecord>(StringComparer.Ordinal);

        await AddQueryCandidatesAsync(query, records, cancellationToken);
        await AddFallbackCandidatesAsync(records, cancellationToken);

        return records.Values.ToArray();
    }

    private async Task AddQueryCandidatesAsync(
        SearchQuery query,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        var normalizedQuery = NormalizeSearchText(query.NormalizedText);
        var containsPattern = $"%{EscapeLike(normalizedQuery)}%";
        var orderedPattern = CreateOrderedLikePattern(normalizedQuery);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            select
                full_path,
                is_directory,
                size_bytes,
                last_write_time
            from files
            where search_text like $contains escape '\'
               or search_text like $ordered escape '\'
            order by name
            limit $limit;
            """;
        command.Parameters.AddWithValue("$contains", containsPattern);
        command.Parameters.AddWithValue("$ordered", orderedPattern);
        command.Parameters.AddWithValue("$limit", QueryCandidateLimit);

        await AddRecordsAsync(command, records, cancellationToken);
    }

    private async Task AddFallbackCandidatesAsync(
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            select
                full_path,
                is_directory,
                size_bytes,
                last_write_time
            from files
            order by name
            limit $limit;
            """;
        command.Parameters.AddWithValue("$limit", FallbackCandidateLimit);

        await AddRecordsAsync(command, records, cancellationToken);
    }

    private static async Task AddRecordsAsync(
        SqliteCommand command,
        IDictionary<string, FileRecord> records,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = FileRecord.Create(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetInt64(2),
                ParseDateTime(reader.GetString(3)));
            records.TryAdd(record.PathKey, record);
        }
    }

    private async Task<IReadOnlyList<UsageRecord>> ReadUsageAsync(CancellationToken cancellationToken)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            select
                full_path,
                open_count,
                last_used_at
            from usage;
            """;

        var records = new List<UsageRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(new UsageRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                ParseDateTime(reader.GetString(2))));
        }

        return records;
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

    private static string CreateRecordSearchText(FileRecord record)
    {
        return string.Join(
            ' ',
            PinyinMatcher.CreateSearchText(record.FullPath),
            PinyinMatcher.CreateSearchText(record.Name));
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
}
