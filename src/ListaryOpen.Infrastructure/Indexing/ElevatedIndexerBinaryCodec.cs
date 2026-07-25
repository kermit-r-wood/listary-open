using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

/// <summary>
/// Length-prefixed binary frames for elevated scan records.
/// Replaces JSONL on the scan path to cut serialize/parse CPU and allocations.
/// </summary>
/// <remarks>
/// Frame layout (little-endian):
/// magic u32 ('LOB1') | flags u8 | pad x3 | size i64 | utcTicks i64 | frn u64 | pathLen i32 | path utf8
/// </remarks>
public static class ElevatedIndexerBinaryCodec
{
    public const uint FrameMagic = 0x31424F4C; // 'LOB1'
    public const int FixedHeaderSize = 36;
    private const byte FlagIsDirectory = 0x01;

    public static int GetFrameSize(ReadOnlySpan<byte> pathUtf8) => FixedHeaderSize + pathUtf8.Length;

    public static int GetFrameSize(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        return FixedHeaderSize + Encoding.UTF8.GetByteCount(fullPath);
    }

    public static bool TryWriteFrame(Span<byte> destination, FileRecord record, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(record);
        var pathByteCount = Encoding.UTF8.GetByteCount(record.FullPath);
        var frameSize = FixedHeaderSize + pathByteCount;
        if (destination.Length < frameSize)
        {
            bytesWritten = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, FrameMagic);
        destination[4] = record.IsDirectory ? FlagIsDirectory : (byte)0;
        destination[5] = 0;
        destination[6] = 0;
        destination[7] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(8), record.SizeBytes);
        BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(16), record.LastWriteTime.UtcTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(24), record.FileReferenceNumber);
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(32), pathByteCount);
        Encoding.UTF8.GetBytes(record.FullPath, destination.Slice(FixedHeaderSize, pathByteCount));
        bytesWritten = frameSize;
        return true;
    }

    public static bool TryReadFrame(ReadOnlySpan<byte> source, out FileRecord? record, out int bytesConsumed)
    {
        record = null;
        bytesConsumed = 0;
        if (source.Length < FixedHeaderSize)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (magic != FrameMagic)
        {
            return false;
        }

        var pathLength = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(32));
        if (pathLength < 0 || pathLength > 32 * 1024)
        {
            return false;
        }

        var frameSize = FixedHeaderSize + pathLength;
        if (source.Length < frameSize)
        {
            return false;
        }

        var flags = source[4];
        var sizeBytes = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(8));
        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(source.Slice(16));
        var frn = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(24));
        var path = Encoding.UTF8.GetString(source.Slice(FixedHeaderSize, pathLength));
        if (string.IsNullOrWhiteSpace(path) || sizeBytes < 0)
        {
            return false;
        }

        DateTimeOffset lastWrite;
        try
        {
            lastWrite = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        try
        {
            record = FileRecord.Create(
                path,
                (flags & FlagIsDirectory) != 0,
                sizeBytes,
                lastWrite,
                frn);
        }
        catch (ArgumentException)
        {
            return false;
        }

        bytesConsumed = frameSize;
        return true;
    }

    /// <summary>
    /// Writes complete frames from a pending+incoming buffer, advancing consumed bytes.
    /// Returns false if magic is corrupt (not a partial-frame case).
    /// </summary>
    public static bool TryConsumeFrames(
        ReadOnlySpan<byte> buffer,
        List<FileRecord> destination,
        out int bytesConsumed,
        out bool corrupt)
    {
        bytesConsumed = 0;
        corrupt = false;
        while (bytesConsumed < buffer.Length)
        {
            var slice = buffer[bytesConsumed..];
            if (slice.Length < FixedHeaderSize)
            {
                break;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(slice);
            if (magic != FrameMagic)
            {
                corrupt = true;
                return false;
            }

            var pathLength = BinaryPrimitives.ReadInt32LittleEndian(slice.Slice(32));
            if (pathLength < 0 || pathLength > 32 * 1024)
            {
                corrupt = true;
                return false;
            }

            var frameSize = FixedHeaderSize + pathLength;
            if (slice.Length < frameSize)
            {
                break;
            }

            if (!TryReadFrame(slice[..frameSize], out var record, out var consumed) || record is null)
            {
                corrupt = true;
                return false;
            }

            destination.Add(record);
            bytesConsumed += consumed;
        }

        return true;
    }
}
