using System.Globalization;
using System.IO;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.Infrastructure.Search;

public sealed class SqliteSearchIndex : ISearchIndex, IAsyncDisposable
{
    private const int CandidateLimit = 5000;

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
        ArgumentNullException.ThrowIfNull(record);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            insert into files (
                full_path,
                path_key,
                name,
                parent_path,
                is_directory,
                size_bytes,
                last_write_time
            )
            values (
                $full_path,
                $path_key,
                $name,
                $parent_path,
                $is_directory,
                $size_bytes,
                $last_write_time
            )
            on conflict(path_key) do update set
                full_path = excluded.full_path,
                name = excluded.name,
                parent_path = excluded.parent_path,
                is_directory = excluded.is_directory,
                size_bytes = excluded.size_bytes,
                last_write_time = excluded.last_write_time;
            """;

        command.Parameters.AddWithValue("$full_path", record.FullPath);
        command.Parameters.AddWithValue("$path_key", record.PathKey);
        command.Parameters.AddWithValue("$name", record.Name);
        command.Parameters.AddWithValue("$parent_path", record.ParentPath);
        command.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        command.Parameters.AddWithValue("$last_write_time", FormatDateTime(record.LastWriteTime));

        await command.ExecuteNonQueryAsync(cancellationToken);
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

        var candidates = await ReadCandidatesAsync(cancellationToken);
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
                full_path text,
                path_key text primary key,
                name text,
                parent_path text,
                is_directory integer,
                size_bytes integer,
                last_write_time text
            );

            create table if not exists usage(
                full_path text,
                path_key text primary key,
                open_count integer,
                last_used_at text
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<FileRecord>> ReadCandidatesAsync(CancellationToken cancellationToken)
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
        command.Parameters.AddWithValue("$limit", CandidateLimit);

        // Keep candidate selection broad so ResultRanker owns fuzzy and pinyin matching.
        var records = new List<FileRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(FileRecord.Create(
                reader.GetString(0),
                reader.GetInt64(1) != 0,
                reader.GetInt64(2),
                ParseDateTime(reader.GetString(3))));
        }

        return records;
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
}
