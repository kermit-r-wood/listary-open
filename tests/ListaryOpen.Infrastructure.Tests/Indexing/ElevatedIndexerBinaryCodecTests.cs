using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerBinaryCodecTests
{
    [Fact]
    public void RoundTripsRecordWithFileReferenceAndDirectoryFlag()
    {
        var original = FileRecord.Create(
            @"C:\Docs\合同.docx",
            isDirectory: false,
            sizeBytes: 42,
            lastWriteTime: new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            fileReferenceNumber: 77);

        var buffer = new byte[ElevatedIndexerBinaryCodec.GetFrameSize(original.FullPath)];
        Assert.True(ElevatedIndexerBinaryCodec.TryWriteFrame(buffer, original, out var written));
        Assert.Equal(buffer.Length, written);

        Assert.True(ElevatedIndexerBinaryCodec.TryReadFrame(buffer, out var decoded, out var consumed));
        Assert.Equal(written, consumed);
        Assert.NotNull(decoded);
        Assert.Equal(original.FullPath, decoded!.FullPath);
        Assert.Equal(original.IsDirectory, decoded.IsDirectory);
        Assert.Equal(original.SizeBytes, decoded.SizeBytes);
        Assert.Equal(original.LastWriteTime.UtcTicks, decoded.LastWriteTime.UtcTicks);
        Assert.Equal(original.FileReferenceNumber, decoded.FileReferenceNumber);
    }

    [Fact]
    public void TryConsumeFramesHandlesPartialTrailingBytes()
    {
        var a = FileRecord.Create(@"C:\a.txt", false, 1, DateTimeOffset.UtcNow, 1);
        var b = FileRecord.Create(@"C:\b.txt", false, 2, DateTimeOffset.UtcNow, 2);
        var frameA = new byte[ElevatedIndexerBinaryCodec.GetFrameSize(a.FullPath)];
        var frameB = new byte[ElevatedIndexerBinaryCodec.GetFrameSize(b.FullPath)];
        Assert.True(ElevatedIndexerBinaryCodec.TryWriteFrame(frameA, a, out _));
        Assert.True(ElevatedIndexerBinaryCodec.TryWriteFrame(frameB, b, out _));

        var combined = new byte[frameA.Length + frameB.Length];
        Buffer.BlockCopy(frameA, 0, combined, 0, frameA.Length);
        Buffer.BlockCopy(frameB, 0, combined, frameA.Length, frameB.Length);

        // Feed all of A and only half of B.
        var partial = combined.AsSpan(0, frameA.Length + (frameB.Length / 2)).ToArray();
        var decoded = new List<FileRecord>();
        Assert.True(ElevatedIndexerBinaryCodec.TryConsumeFrames(partial, decoded, out var consumed, out var corrupt));
        Assert.False(corrupt);
        Assert.Equal(frameA.Length, consumed);
        Assert.Single(decoded);
        Assert.Equal(@"C:\a.txt", decoded[0].FullPath);
    }

    [Fact]
    public async Task BinaryWriterProducesDecodableStream()
    {
        var records = new[]
        {
            FileRecord.Create(@"C:\Docs\one.txt", false, 10, DateTimeOffset.UtcNow, 10),
            FileRecord.Create(@"C:\Docs\two", true, 0, DateTimeOffset.UtcNow, 11)
        };

        await using var stream = new MemoryStream();
        await ElevatedIndexerBinaryWriter.WriteRecordsAsync(
            ToAsync(records),
            stream,
            CancellationToken.None);

        var bytes = stream.ToArray();
        var decoded = new List<FileRecord>();
        Assert.True(ElevatedIndexerBinaryCodec.TryConsumeFrames(bytes, decoded, out var consumed, out var corrupt));
        Assert.False(corrupt);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(@"C:\Docs\one.txt", decoded[0].FullPath);
        Assert.True(decoded[1].IsDirectory);
    }

    private static async IAsyncEnumerable<FileRecord> ToAsync(IEnumerable<FileRecord> records)
    {
        foreach (var record in records)
        {
            yield return record;
            await Task.Yield();
        }
    }
}
