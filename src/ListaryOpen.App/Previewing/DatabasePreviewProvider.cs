using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.App.Previewing;

internal sealed class DatabasePreviewProvider : IFilePreviewProvider
{
    private static ReadOnlySpan<byte> SqliteMagic => "SQLite format 3\0"u8;

    public bool CanPreview(PreviewContext context) =>
        PreviewFormatRegistry.Supports(context.Extension, PreviewFallback.Database);

    public async Task<PreviewContent?> LoadAsync(
        PreviewContext context,
        CancellationToken cancellationToken)
    {
        var header = new byte[100];
        await using var stream = new FileStream(
            context.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: header.Length,
            useAsync: true);
        var count = await stream.ReadAtLeastAsync(
            header,
            header.Length,
            throwOnEndOfStream: false,
            cancellationToken).ConfigureAwait(false);
        if (context.Extension.Equals(".dbf", StringComparison.OrdinalIgnoreCase))
        {
            return await LoadDbfAsync(stream, header.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
        }
        if (count < header.Length || !header.AsSpan(0, SqliteMagic.Length).SequenceEqual(SqliteMagic))
        {
            return null;
        }

        var encodedPageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
        var pageSize = encodedPageSize == 1 ? 65_536 : encodedPageSize;
        var pageCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4));
        var encoding = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(56, 4)) switch
        {
            1 => "UTF-8",
            2 => "UTF-16 little-endian",
            3 => "UTF-16 big-endian",
            _ => "unspecified"
        };
        var userVersion = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(60, 4));
        var applicationId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(68, 4));
        var builder = new StringBuilder()
            .AppendLine("SQLite 3 database")
            .AppendLine()
            .Append("Size: ").AppendLine(FilePreviewPane.FormatSize(context.SizeBytes))
            .Append("Page size: ").Append(pageSize).AppendLine(" bytes")
            .Append("Database pages: ").AppendLine(pageCount.ToString())
            .Append("Text encoding: ").AppendLine(encoding)
            .Append("File format write version: ").AppendLine(header[18].ToString())
            .Append("File format read version: ").AppendLine(header[19].ToString())
            .Append("User version: ").AppendLine(userVersion.ToString())
            .Append("Application ID: 0x").AppendLine(applicationId.ToString("X8"));
        await AppendSqliteSchemaAsync(builder, context.FullPath, cancellationToken).ConfigureAwait(false);
        return PreviewContent.ForText("SQLite metadata", builder.ToString());
    }

    private static async Task AppendSqliteSchemaAsync(
        StringBuilder builder,
        string path,
        CancellationToken cancellationToken)
    {
        const int maximumSchemaObjects = 200;
        const int maximumOutputCharacters = 120_000;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 2
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var queryOnly = connection.CreateCommand())
            {
                queryOnly.CommandText = "PRAGMA query_only=ON;";
                queryOnly.CommandTimeout = 2;
                await queryOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT type, name, tbl_name, sql
                FROM sqlite_schema
                WHERE name NOT LIKE 'sqlite_%'
                ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name
                LIMIT 200;
                """;
            command.CommandTimeout = 2;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            builder.AppendLine().AppendLine("Schema declarations:");
            var count = 0;
            while (count < maximumSchemaObjects &&
                   await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                var table = reader.GetString(2);
                var sql = reader.IsDBNull(3) ? null : reader.GetString(3);
                builder.Append("• ").Append(type).Append(' ').Append(name);
                if (!string.Equals(name, table, StringComparison.Ordinal))
                {
                    builder.Append(" on ").Append(table);
                }
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(sql))
                {
                    var normalized = string.Join(' ', sql.Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries));
                    builder.Append("  ").AppendLine(
                        normalized.Length <= 4096 ? normalized : normalized[..4096] + "…");
                }
                count++;
                if (builder.Length >= maximumOutputCharacters)
                {
                    builder.Length = maximumOutputCharacters;
                    builder.AppendLine().AppendLine("… schema output truncated");
                    break;
                }
            }
            if (count == maximumSchemaObjects)
            {
                builder.AppendLine("… additional schema objects omitted");
            }
            builder.AppendLine()
                .AppendLine("Read-only sqlite_schema query; declarations are displayed but never executed. User rows are never read.");
        }
        catch (SqliteException exception)
        {
            builder.AppendLine().AppendLine("Schema could not be read safely: ")
                .AppendLine(exception.SqliteErrorCode.ToString())
                .AppendLine("Header metadata remains available; no write was attempted.");
        }
    }

    private static async Task<PreviewContent?> LoadDbfAsync(
        FileStream stream,
        ReadOnlyMemory<byte> initial,
        CancellationToken cancellationToken)
    {
        const int maximumFields = 256;
        if (initial.Length < 32)
        {
            return null;
        }
        var first = initial.ToArray();
        var headerLength = (ushort)(first[8] | first[9] << 8);
        var recordLength = (ushort)(first[10] | first[11] << 8);
        if (headerLength < 33 || headerLength > 32 + maximumFields * 32 + 1 ||
            headerLength > stream.Length || (headerLength - 33) % 32 != 0 || recordLength == 0)
        {
            return null;
        }
        var header = new byte[headerLength];
        stream.Position = 0;
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        return ParseDbf(header, recordLength, cancellationToken);
    }

    private static PreviewContent? ParseDbf(
        byte[] header,
        ushort recordLength,
        CancellationToken cancellationToken)
    {
        if (header[^1] != 0x0D)
        {
            return null;
        }
        var fieldCount = (header.Length - 33) / 32;
        var declaredRecordWidth = 1;
        var builder = new StringBuilder()
            .AppendLine("dBASE table")
            .AppendLine()
            .Append("Format: ").AppendLine(DescribeDbfVersion(header[0]))
            .Append("Last update: ").Append(1900 + header[1]).Append('-')
                .Append(header[2].ToString("D2")).Append('-').AppendLine(header[3].ToString("D2"))
            .Append("Records declared: ")
                .AppendLine(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)).ToString())
            .Append("Record length: ").Append(recordLength).AppendLine(" bytes")
            .Append("Fields: ").AppendLine(fieldCount.ToString())
            .Append("Code-page mark: 0x").AppendLine(header[29].ToString("X2"))
            .AppendLine()
            .AppendLine("Schema:");
        for (var index = 0; index < fieldCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = header.AsSpan(32 + index * 32, 32);
            var nameLength = field[..11].IndexOf((byte)0);
            if (nameLength < 0)
            {
                nameLength = 11;
            }
            var name = Encoding.ASCII.GetString(field[..nameLength]).Trim();
            var width = field[16];
            var decimals = field[17];
            declaredRecordWidth += width;
            builder.Append("• ").Append(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name)
                .Append(" — ").Append((char)field[11])
                .Append(", width ").Append(width);
            if (decimals > 0)
            {
                builder.Append(", scale ").Append(decimals);
            }
            builder.AppendLine();
        }
        if (declaredRecordWidth > recordLength)
        {
            return null;
        }
        builder.AppendLine()
            .AppendLine("Schema metadata only; memo sidecars and row values are never opened.");
        return PreviewContent.ForText("DBF schema", builder.ToString());
    }

    private static string DescribeDbfVersion(byte version) => version switch
    {
        0x02 => "FoxBASE",
        0x03 or 0x83 => "dBASE III",
        0x04 or 0x8B => "dBASE IV",
        0x05 => "dBASE V",
        0x30 or 0x31 or 0x32 => "Visual FoxPro",
        _ => $"DBF version 0x{version:X2}"
    };
}
