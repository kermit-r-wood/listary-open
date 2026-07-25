using System.Text.Json;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Indexer.Elevated;

internal static class ElevatedIndexerRecordWriter
{
    // Flush complete JSONL batches often enough for the non-elevated process to tail
    // the file, without paying for a kernel write for every record.
    internal const int StreamingFlushRecordCount = 256;
    private const int StreamBufferSize = 64 * 1024;

    public static async Task WriteAsync(
        IAsyncEnumerable<FileRecord> records,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(writer);

        var recordsSinceFlush = 0;
        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var line = JsonSerializer.Serialize(
                new
                {
                    record.FullPath,
                    record.IsDirectory,
                    record.SizeBytes,
                    record.LastWriteTime,
                    fileReferenceNumber = record.FileReferenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                },
                JsonOptions.Default);

            await writer.WriteLineAsync(line).ConfigureAwait(false);
            recordsSinceFlush++;
            if (recordsSinceFlush == StreamingFlushRecordCount)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                recordsSinceFlush = 0;
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteFileAsync(
        IAsyncEnumerable<FileRecord> records,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(outputPath, ".jsonl");
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StreamBufferSize,
            leaveOpen: false);
        await WriteAsync(records, writer, cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteJournalStateFileAsync(
        UsnJournalState state,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(outputPath, ".jsonl");
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StreamBufferSize,
            leaveOpen: false);
        var line = JsonSerializer.Serialize(
            new
            {
                state.UsnJournalId,
                state.LowestValidUsn,
                state.NextUsn
            },
            JsonOptions.Default);

        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    public static async Task WriteJournalChangesFileAsync(
        IAsyncEnumerable<UsnJournalChange> changes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(outputPath, ".jsonl");
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StreamBufferSize,
            leaveOpen: false);
        var changesSinceFlush = 0;
        await foreach (var change in changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var line = JsonSerializer.Serialize(CreateChangeDto(change), JsonOptions.Default);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            changesSinceFlush++;
            if (changesSinceFlush == StreamingFlushRecordCount)
            {
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                changesSinceFlush = 0;
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object CreateChangeDto(UsnJournalChange change)
    {
        return change.Kind switch
        {
            UsnJournalChangeKind.Upsert => new
            {
                kind = "upsert",
                change.Record!.FullPath,
                change.Record.IsDirectory,
                change.Record.SizeBytes,
                change.Record.LastWriteTime,
                fileReferenceNumber = change.Record.FileReferenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            UsnJournalChangeKind.Delete => new
            {
                kind = "delete",
                change.FullPath
            },
            UsnJournalChangeKind.FileRename => new
            {
                kind = "fileRename",
                change.OldFullPath,
                change.Record!.FullPath,
                change.Record.IsDirectory,
                change.Record.SizeBytes,
                change.Record.LastWriteTime,
                fileReferenceNumber = change.Record.FileReferenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            UsnJournalChangeKind.DirectoryRenameOrMove => new
            {
                kind = "directoryRenameOrMove"
            },
            UsnJournalChangeKind.HardLinkResync => new
            {
                kind = "hardLinkResync",
                fileReferenceNumber = change.FileReferenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                liveRecords = (change.LiveHardLinkRecords ?? Array.Empty<FileRecord>()).Select(record => new
                {
                    record.FullPath,
                    record.IsDirectory,
                    record.SizeBytes,
                    record.LastWriteTime,
                    fileReferenceNumber = record.FileReferenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }).ToArray()
            },
            _ => throw new NotSupportedException($"Unsupported journal change kind: {change.Kind}.")
        };
    }
}
