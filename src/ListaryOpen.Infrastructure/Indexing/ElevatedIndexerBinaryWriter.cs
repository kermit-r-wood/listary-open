using System.IO;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

/// <summary>
/// Streaming binary writer for elevated scan output (.bin).
/// </summary>
public static class ElevatedIndexerBinaryWriter
{
    public const int StreamingFlushRecordCount = 256;
    private const int StreamBufferSize = 256 * 1024;

    public static async Task WriteRecordsAsync(
        IAsyncEnumerable<FileRecord> records,
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[StreamBufferSize];
        var offset = 0;
        var recordsSinceFlush = 0;

        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var frameSize = ElevatedIndexerBinaryCodec.GetFrameSize(record.FullPath);
            if (frameSize > buffer.Length)
            {
                if (offset > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                    offset = 0;
                }

                var large = new byte[frameSize];
                if (!ElevatedIndexerBinaryCodec.TryWriteFrame(large, record, out _))
                {
                    throw new InvalidOperationException("Failed to encode elevated indexer binary frame.");
                }

                await stream.WriteAsync(large, cancellationToken).ConfigureAwait(false);
                recordsSinceFlush++;
            }
            else
            {
                if (offset + frameSize > buffer.Length)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                    offset = 0;
                }

                if (!ElevatedIndexerBinaryCodec.TryWriteFrame(buffer.AsSpan(offset), record, out var written))
                {
                    throw new InvalidOperationException("Failed to encode elevated indexer binary frame.");
                }

                offset += written;
                recordsSinceFlush++;
            }

            if (recordsSinceFlush >= StreamingFlushRecordCount)
            {
                if (offset > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
                    offset = 0;
                }

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                recordsSinceFlush = 0;
            }
        }

        if (offset > 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
