using System.Buffers;
using System.Globalization;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.IndexingBenchmark;

internal static class ScanOutputAnalyzer
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: --analyze-scan-output DB_PATH BIN_PATH SKIP COUNT");
            return 2;
        }

        var databasePath = Path.GetFullPath(args[0]);
        var binaryPath = Path.GetFullPath(args[1]);
        var skip = ParseNonNegativeInt(args[2], "SKIP");
        var count = ParsePositiveInt(args[3], "COUNT");
        var records = await ReadBinaryRecordsAsync(binaryPath, skip, count, cancellationToken)
            .ConfigureAwait(false);
        var existing = await ReadExistingRecordsAsync(databasePath, records, cancellationToken)
            .ConfigureAwait(false);

        var missing = 0;
        var unchanged = 0;
        var fullPathChanged = 0;
        var nameChanged = 0;
        var parentChanged = 0;
        var searchTextChanged = 0;
        var kindChanged = 0;
        var sizeChanged = 0;
        var timestampChanged = 0;
        var referenceChanged = 0;
        var samples = new List<string>();

        foreach (var record in records)
        {
            if (!existing.TryGetValue(record.PathKey, out var stored))
            {
                missing++;
                AddSample($"missing: {record.FullPath}");
                continue;
            }

            var different = false;
            Check(stored.FullPath != record.FullPath, ref fullPathChanged, "path");
            Check(stored.Name != record.Name, ref nameChanged, "name");
            Check(stored.ParentPath != record.ParentPath, ref parentChanged, "parent");
            Check(stored.SearchText != PinyinMatcher.CreateSearchAliases(record.FullPath), ref searchTextChanged, "search");
            Check(stored.IsDirectory != record.IsDirectory, ref kindChanged, "kind");
            Check(stored.SizeBytes != record.SizeBytes, ref sizeChanged, "size");
            Check(stored.LastWriteTime != FormatDateTime(record.LastWriteTime), ref timestampChanged, "time");
            Check(stored.FileReference != unchecked((long)record.FileReferenceNumber), ref referenceChanged, "reference");
            if (!different)
            {
                unchanged++;
            }

            void Check(bool condition, ref int counter, string field)
            {
                if (!condition)
                {
                    return;
                }

                different = true;
                counter++;
                AddSample($"{field}: {record.FullPath}");
            }
        }

        Console.WriteLine($"records={records.Length:N0} existing={existing.Count:N0} missing={missing:N0} unchanged={unchanged:N0}");
        Console.WriteLine(
            $"changed path={fullPathChanged:N0} name={nameChanged:N0} parent={parentChanged:N0} " +
            $"search={searchTextChanged:N0} kind={kindChanged:N0} size={sizeChanged:N0} " +
            $"time={timestampChanged:N0} reference={referenceChanged:N0}");
        foreach (var sample in samples)
        {
            Console.WriteLine(sample);
        }

        return 0;

        void AddSample(string value)
        {
            if (samples.Count < 12 && !samples.Contains(value, StringComparer.Ordinal))
            {
                samples.Add(value);
            }
        }
    }

    private static async Task<FileRecord[]> ReadBinaryRecordsAsync(
        string path,
        int skip,
        int count,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 256 * 1024;
        var readBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var pending = ArrayPool<byte>.Shared.Rent(bufferSize * 2);
        var pendingLength = 0;
        var seen = 0;
        var result = new List<FileRecord>(count);
        var decoded = new List<FileRecord>(2048);

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize,
                useAsync: true);
            while (result.Count < count)
            {
                var read = await stream.ReadAsync(readBuffer.AsMemory(0, bufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                EnsureCapacity(ref pending, pendingLength + read);
                Buffer.BlockCopy(readBuffer, 0, pending, pendingLength, read);
                pendingLength += read;
                decoded.Clear();
                _ = ElevatedIndexerBinaryCodec.TryConsumeFrames(
                    pending.AsSpan(0, pendingLength),
                    decoded,
                    out var consumed,
                    out var corrupt);
                if (corrupt)
                {
                    throw new InvalidDataException("Indexer scan output contains a corrupt frame.");
                }

                if (consumed > 0)
                {
                    var remaining = pendingLength - consumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(pending, consumed, pending, 0, remaining);
                    }

                    pendingLength = remaining;
                }

                foreach (var record in decoded)
                {
                    if (seen++ >= skip && result.Count < count)
                    {
                        result.Add(record);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            ArrayPool<byte>.Shared.Return(pending);
        }

        if (result.Count != count)
        {
            throw new InvalidDataException(
                $"Requested {count:N0} records after offset {skip:N0}, but read {result.Count:N0}.");
        }

        return result.ToArray();
    }

    private static async Task<Dictionary<string, StoredRecord>> ReadExistingRecordsAsync(
        string databasePath,
        IReadOnlyList<FileRecord> records,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, StoredRecord>(records.Count, StringComparer.Ordinal);
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var chunk in records.Chunk(400))
        {
            using var command = connection.CreateCommand();
            var names = new string[chunk.Length];
            for (var index = 0; index < chunk.Length; index++)
            {
                names[index] = $"$path_{index}";
                command.Parameters.AddWithValue(names[index], chunk[index].PathKey);
            }

            command.CommandText = $"""
                select path_key, full_path, name, parent_path, search_text,
                       is_directory, size_bytes, last_write_time, file_reference
                from files
                where path_key in ({string.Join(", ", names)});
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result[reader.GetString(0)] = new StoredRecord(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5) != 0,
                    reader.GetInt64(6),
                    reader.GetString(7),
                    reader.GetInt64(8));
            }
        }

        return result;
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (buffer.Length >= required)
        {
            return;
        }

        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(required, buffer.Length * 2));
        Buffer.BlockCopy(buffer, 0, replacement, 0, buffer.Length);
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = replacement;
    }

    private static string FormatDateTime(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static int ParsePositiveInt(string value, string name)
    {
        var result = ParseNonNegativeInt(value, name);
        return result > 0 ? result : throw new ArgumentOutOfRangeException(name, "Value must be positive.");
    }

    private static int ParseNonNegativeInt(string value, string name)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0
            ? result
            : throw new ArgumentException($"{name} must be a non-negative integer.");

    private sealed record StoredRecord(
        string FullPath,
        string Name,
        string ParentPath,
        string SearchText,
        bool IsDirectory,
        long SizeBytes,
        string LastWriteTime,
        long FileReference);
}
