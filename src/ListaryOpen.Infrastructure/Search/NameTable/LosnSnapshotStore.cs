using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Search.NameTable;

/// <summary>
/// LOSN v2: sectioned compact binary storage with bounded streaming verification.
/// No FileRecord[], JSON payload, or verification Engine is constructed.
/// </summary>
public sealed class LosnSnapshotStore
{
    public const uint Magic = 0x4E534F4C; // 'LOSN' little-endian
    public const int FormatVersion = 2;

    private const int HeaderSize = 128;
    private const int DescriptorSize = 72;
    private const uint SupportedRequiredFlags = 0x0000_0001;
    private const uint RequiredSectionFlag = 0x0000_0001;
    private const int HashSize = 32;
    private const int VerificationBufferSize = 1024 * 1024;
    private const int MaximumCheckpointStringBytes = 1024 * 1024;
    private const int SectionCount = 21;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public Task SaveAsync(
        string snapshotPath,
        NameTableEngine engine,
        CancellationToken cancellationToken) =>
        SaveCoreAsync(snapshotPath, engine, checkpoint: null, cancellationToken);

    public Task SaveAsync(
        string snapshotPath,
        NameTableEngine engine,
        UsnJournalCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return SaveCoreAsync(snapshotPath, engine, checkpoint, cancellationToken);
    }

    public async Task LoadIntoAsync(
        string snapshotPath,
        NameTableEngine engine,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentNullException.ThrowIfNull(engine);

        Exception? primaryFailure = null;
        if (File.Exists(snapshotPath))
        {
            try
            {
                await LoadSingleAsync(snapshotPath, engine, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                primaryFailure = exception;
            }
        }

        var backupPath = snapshotPath + ".bak";
        if (File.Exists(backupPath))
        {
            try
            {
                await LoadSingleAsync(backupPath, engine, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception backupFailure) when (backupFailure is InvalidDataException or IOException)
            {
                throw new InvalidDataException(
                    "Both the primary and backup LOSN snapshots are invalid.",
                    new AggregateException(primaryFailure ?? new FileNotFoundException(snapshotPath), backupFailure));
            }
        }

        if (primaryFailure is not null)
        {
            throw new InvalidDataException("The primary LOSN snapshot is invalid and no backup is available.", primaryFailure);
        }

        throw new FileNotFoundException("LOSN snapshot was not found.", snapshotPath);
    }

    /// <summary>Resume USN must never exceed the durable snapshot cursor.</summary>
    public static long ResumeUsn(long snapshotWatermarkUsn, long metaCheckpointUsn)
        => Math.Min(snapshotWatermarkUsn, metaCheckpointUsn);

    private static async Task SaveCoreAsync(
        string snapshotPath,
        NameTableEngine engine,
        UsnJournalCheckpoint? checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        ArgumentNullException.ThrowIfNull(engine);

        var directory = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // The candidate checkpoint is captured into the immutable image, but is
        // not published as durable in memory until replace and post-check succeed.
        var snapshot = engine.CaptureCompactSnapshot(checkpoint);
        var tempPath = snapshotPath + ".tmp";
        var backupPath = snapshotPath + ".bak";

        await WriteSnapshotAsync(tempPath, snapshot, cancellationToken).ConfigureAwait(false);
        var verified = await VerifyAsync(tempPath, cancellationToken).ConfigureAwait(false);
        if (verified.Generation != snapshot.Generation)
        {
            throw new InvalidDataException("LOSN verification returned a different generation.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(snapshotPath))
        {
            File.Replace(tempPath, snapshotPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, snapshotPath);
        }

        // Re-read committed identity before advancing the in-memory durable table.
        var committedHeader = await ReadHeaderAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        if (committedHeader.Generation != snapshot.Generation
            || committedHeader.FileLength != new FileInfo(snapshotPath).Length)
        {
            throw new InvalidDataException("Committed LOSN identity verification failed.");
        }

        engine.MarkSnapshotDurable(snapshot);
    }

    private static async Task WriteSnapshotAsync(
        string tempPath,
        NameTableCompactSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var tableLength = checked(SectionCount * DescriptorSize);
        await using var file = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            VerificationBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        file.SetLength(0);
        file.Write(new byte[HeaderSize + tableLength]);
        var descriptors = new List<SectionDescriptor>(SectionCount);

        WriteSection(file, descriptors, SectionType.Checkpoints, snapshot.Checkpoints.Length, 0, writer =>
        {
            writer.WriteInt32(snapshot.Checkpoints.Length);
            foreach (var checkpoint in snapshot.Checkpoints)
            {
                writer.WriteString(checkpoint.VolumeRoot);
                writer.WriteString(checkpoint.FileSystemName);
                writer.WriteUInt64(checkpoint.UsnJournalId);
                writer.WriteInt64(checkpoint.NextUsn);
                writer.WriteInt64(checkpoint.RulesVersion);
                writer.WriteInt64(checkpoint.LastFullScanUnixMilliseconds);
            }
        });
        WriteArraySection(file, descriptors, SectionType.Entries, snapshot.Entries);
        WriteByteSection(file, descriptors, SectionType.NameHeap, snapshot.Names.Heap);
        WriteArraySection(file, descriptors, SectionType.NameOffsets, snapshot.Names.Offsets);
        WriteArraySection(file, descriptors, SectionType.NameOrder, snapshot.NameOrder);
        WriteArraySection(file, descriptors, SectionType.ShortSearchNameIds, snapshot.ShortSearchNameIds);
        WriteArraySection(file, descriptors, SectionType.RootIds, snapshot.RootIds);
        WriteArraySection(file, descriptors, SectionType.FrnOrder, snapshot.FrnOrder);
        WriteArraySection(file, descriptors, SectionType.NameRecordOffsets, snapshot.NameRecordOffsets);
        WriteArraySection(file, descriptors, SectionType.NameRecordIds, snapshot.NameRecordIds);
        WriteArraySection(file, descriptors, SectionType.NameRecordIdsById, snapshot.NameRecordIdsById);
        WriteArraySection(file, descriptors, SectionType.ChildOffsets, snapshot.ChildOffsets);
        WriteArraySection(file, descriptors, SectionType.ChildIds, snapshot.ChildIds);
        WriteArraySection(file, descriptors, SectionType.TrigramKeys, snapshot.Trigrams.Keys);
        WriteArraySection(file, descriptors, SectionType.TrigramByteOffsets, snapshot.Trigrams.ByteOffsets);
        WriteArraySection(file, descriptors, SectionType.TrigramPostingCounts, snapshot.Trigrams.PostingCounts);
        WriteByteSection(file, descriptors, SectionType.TrigramPayload, snapshot.Trigrams.Payload);
        WriteArraySection(file, descriptors, SectionType.ShortAliasKeys, snapshot.ShortAliases.Keys);
        WriteArraySection(file, descriptors, SectionType.ShortAliasByteOffsets, snapshot.ShortAliases.ByteOffsets);
        WriteArraySection(file, descriptors, SectionType.ShortAliasPostingCounts, snapshot.ShortAliases.PostingCounts);
        WriteByteSection(file, descriptors, SectionType.ShortAliasPayload, snapshot.ShortAliases.Payload);

        if (descriptors.Count != SectionCount)
        {
            throw new InvalidDataException("LOSN section count mismatch while writing.");
        }

        var fileLength = file.Position;
        var table = EncodeSectionTable(descriptors);
        var tableHash = SHA256.HashData(table);
        var header = EncodeHeader(snapshot, fileLength, table.Length, tableHash);
        file.Position = 0;
        file.Write(header);
        file.Write(table);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    private static async Task LoadSingleAsync(
        string path,
        NameTableEngine engine,
        CancellationToken cancellationToken)
    {
        var layout = await VerifyAsync(path, cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            VerificationBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        var checkpoints = ReadCheckpoints(file, layout.Required(SectionType.Checkpoints), cancellationToken);
        var entries = ReadArray<CompactFileEntry>(file, layout.Required(SectionType.Entries), cancellationToken);
        var nameHeap = ReadBytes(file, layout.Required(SectionType.NameHeap), cancellationToken);
        var nameOffsets = ReadArray<int>(file, layout.Required(SectionType.NameOffsets), cancellationToken);
        var nameOrder = ReadArray<int>(file, layout.Required(SectionType.NameOrder), cancellationToken);
        var shortSearchNameIds = ReadArray<int>(file, layout.Required(SectionType.ShortSearchNameIds), cancellationToken);
        var rootIds = ReadArray<int>(file, layout.Required(SectionType.RootIds), cancellationToken);
        var frnOrder = ReadArray<int>(file, layout.Required(SectionType.FrnOrder), cancellationToken);
        var nameRecordOffsets = ReadArray<int>(file, layout.Required(SectionType.NameRecordOffsets), cancellationToken);
        var nameRecordIds = ReadArray<int>(file, layout.Required(SectionType.NameRecordIds), cancellationToken);
        var nameRecordIdsById = ReadArray<int>(file, layout.Required(SectionType.NameRecordIdsById), cancellationToken);
        var childOffsets = ReadArray<int>(file, layout.Required(SectionType.ChildOffsets), cancellationToken);
        var childIds = ReadArray<int>(file, layout.Required(SectionType.ChildIds), cancellationToken);
        var trigramKeys = ReadArray<uint>(file, layout.Required(SectionType.TrigramKeys), cancellationToken);
        var trigramOffsets = ReadArray<int>(file, layout.Required(SectionType.TrigramByteOffsets), cancellationToken);
        var trigramCounts = ReadArray<int>(file, layout.Required(SectionType.TrigramPostingCounts), cancellationToken);
        var trigramPayload = ReadBytes(file, layout.Required(SectionType.TrigramPayload), cancellationToken);
        var shortAliasKeys = ReadArray<uint>(file, layout.Required(SectionType.ShortAliasKeys), cancellationToken);
        var shortAliasOffsets = ReadArray<int>(file, layout.Required(SectionType.ShortAliasByteOffsets), cancellationToken);
        var shortAliasCounts = ReadArray<int>(file, layout.Required(SectionType.ShortAliasPostingCounts), cancellationToken);
        var shortAliasPayload = ReadBytes(file, layout.Required(SectionType.ShortAliasPayload), cancellationToken);

        var snapshot = new NameTableCompactSnapshot(
            layout.Generation,
            checked((int)layout.EntryCount),
            checked((int)layout.LiveCount),
            checked((int)layout.TombstoneCount),
            entries,
            new CompactNameStoreSnapshot(nameHeap, nameHeap.Length, nameOffsets, checked((int)layout.NameCount)),
            nameOrder,
            shortSearchNameIds,
            rootIds,
            frnOrder,
            nameRecordOffsets,
            nameRecordIds,
            nameRecordIdsById,
            childOffsets,
            childIds,
            new CompactTrigramSnapshot(trigramKeys, trigramOffsets, trigramCounts, trigramPayload),
            new CompactTrigramSnapshot(shortAliasKeys, shortAliasOffsets, shortAliasCounts, shortAliasPayload),
            checkpoints);
        engine.RestoreCompactSnapshot(snapshot);
    }

    private static async Task<SnapshotLayout> VerifyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(path, cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            VerificationBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (file.Length != header.FileLength)
        {
            throw new InvalidDataException("LOSN file length does not match its header.");
        }

        var tableLength = checked(header.SectionCount * DescriptorSize);
        if (header.SectionTableOffset != HeaderSize
            || header.TableLength != tableLength
            || header.SectionTableOffset > file.Length - tableLength)
        {
            throw new InvalidDataException("LOSN section table bounds are invalid.");
        }

        var table = new byte[tableLength];
        file.Position = header.SectionTableOffset;
        await file.ReadExactlyAsync(table, cancellationToken).ConfigureAwait(false);
        var actualTableHash = SHA256.HashData(table);
        if (!CryptographicOperations.FixedTimeEquals(header.TableHash, actualTableHash))
        {
            throw new InvalidDataException("LOSN section table checksum mismatch.");
        }

        var descriptors = DecodeSectionTable(table);
        ValidateDescriptors(descriptors, file.Length, HeaderSize + tableLength);
        var buffer = ArrayPool<byte>.Shared.Rent(VerificationBufferSize);
        try
        {
            foreach (var descriptor in descriptors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                file.Position = descriptor.Offset;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var remaining = descriptor.Length;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(remaining, buffer.Length);
                    await file.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    remaining -= count;
                }

                var actual = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(descriptor.Hash, actual))
                {
                    throw new InvalidDataException($"LOSN section {descriptor.Type} checksum mismatch.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new SnapshotLayout(
            header.Generation,
            header.EntryCount,
            header.LiveCount,
            header.TombstoneCount,
            header.NameCount,
            descriptors.ToDictionary(item => item.Type));
    }

    private static async Task<SnapshotHeader> ReadHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            HeaderSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var bytes = new byte[HeaderSize];
        await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (magic != Magic)
        {
            throw new InvalidDataException($"LOSN magic mismatch: 0x{magic:X8}.");
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported LOSN version {version}; a full rebuild is required.");
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var requiredFlags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        if (headerSize != HeaderSize || (requiredFlags & ~SupportedRequiredFlags) != 0)
        {
            throw new InvalidDataException("LOSN header size or required flags are unsupported.");
        }

        var fileLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16));
        var generation = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24));
        var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(32));
        var sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40));
        var checkpointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(44));
        var entryCount = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48));
        var liveCount = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56));
        var tombstoneCount = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64));
        var nameCount = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(72));
        var tableLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(80));
        if (fileLength < HeaderSize
            || generation < 0
            || sectionCount != SectionCount
            || checkpointCount < 0
            || entryCount < 0
            || entryCount > int.MaxValue
            || liveCount < 0
            || liveCount > entryCount
            || tombstoneCount < 0
            || tombstoneCount > int.MaxValue
            || nameCount < 0
            || nameCount > int.MaxValue)
        {
            throw new InvalidDataException("LOSN header dimensions are invalid.");
        }

        return new SnapshotHeader(
            fileLength,
            generation,
            tableOffset,
            sectionCount,
            checkpointCount,
            entryCount,
            liveCount,
            tombstoneCount,
            nameCount,
            tableLength,
            bytes.AsSpan(88, HashSize).ToArray());
    }

    private static byte[] EncodeHeader(
        NameTableCompactSnapshot snapshot,
        long fileLength,
        int tableLength,
        byte[] tableHash)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), SupportedRequiredFlags);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), fileLength);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), snapshot.Generation);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), SectionCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(44), snapshot.Checkpoints.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), snapshot.EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(56), snapshot.LiveCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(64), snapshot.TombstoneCount);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(72), snapshot.Names.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(80), tableLength);
        tableHash.CopyTo(header.AsSpan(88, HashSize));
        return header;
    }

    private static byte[] EncodeSectionTable(IReadOnlyList<SectionDescriptor> descriptors)
    {
        var bytes = new byte[checked(descriptors.Count * DescriptorSize)];
        for (var i = 0; i < descriptors.Count; i++)
        {
            var destination = bytes.AsSpan(i * DescriptorSize, DescriptorSize);
            var descriptor = descriptors[i];
            BinaryPrimitives.WriteInt32LittleEndian(destination, (int)descriptor.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], descriptor.Flags);
            BinaryPrimitives.WriteInt64LittleEndian(destination[8..], descriptor.Offset);
            BinaryPrimitives.WriteInt64LittleEndian(destination[16..], descriptor.Length);
            BinaryPrimitives.WriteInt64LittleEndian(destination[24..], descriptor.Count);
            BinaryPrimitives.WriteInt32LittleEndian(destination[32..], descriptor.ElementSize);
            descriptor.Hash.CopyTo(destination[40..]);
        }

        return bytes;
    }

    private static IReadOnlyList<SectionDescriptor> DecodeSectionTable(byte[] table)
    {
        if (table.Length % DescriptorSize != 0)
        {
            throw new InvalidDataException("LOSN section table length is invalid.");
        }

        var descriptors = new SectionDescriptor[table.Length / DescriptorSize];
        for (var i = 0; i < descriptors.Length; i++)
        {
            var source = table.AsSpan(i * DescriptorSize, DescriptorSize);
            var typeValue = BinaryPrimitives.ReadInt32LittleEndian(source);
            if (!Enum.IsDefined(typeof(SectionType), typeValue))
            {
                throw new InvalidDataException($"Unknown required LOSN section {typeValue}.");
            }

            descriptors[i] = new SectionDescriptor(
                (SectionType)typeValue,
                BinaryPrimitives.ReadUInt32LittleEndian(source[4..]),
                BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
                BinaryPrimitives.ReadInt32LittleEndian(source[32..]),
                source.Slice(40, HashSize).ToArray());
        }

        return descriptors;
    }

    private static void ValidateDescriptors(
        IReadOnlyList<SectionDescriptor> descriptors,
        long fileLength,
        long minimumOffset)
    {
        if (descriptors.Count != SectionCount
            || descriptors.Select(item => item.Type).Distinct().Count() != SectionCount)
        {
            throw new InvalidDataException("LOSN required sections are missing or duplicated.");
        }

        long previousEnd = minimumOffset;
        foreach (var descriptor in descriptors.OrderBy(item => item.Offset))
        {
            if ((descriptor.Flags & RequiredSectionFlag) == 0
                || (descriptor.Flags & ~RequiredSectionFlag) != 0
                || descriptor.Offset < previousEnd
                || descriptor.Length < 0
                || descriptor.Offset > fileLength - descriptor.Length
                || descriptor.Count < 0
                || descriptor.Count > int.MaxValue
                || descriptor.ElementSize < 0)
            {
                throw new InvalidDataException($"LOSN section {descriptor.Type} bounds are invalid.");
            }

            if (descriptor.ElementSize > 0
                && descriptor.Length != checked(descriptor.Count * descriptor.ElementSize))
            {
                throw new InvalidDataException($"LOSN section {descriptor.Type} element dimensions are invalid.");
            }

            previousEnd = checked(descriptor.Offset + descriptor.Length);
        }

        if (previousEnd != fileLength)
        {
            throw new InvalidDataException("LOSN contains an unreferenced gap or trailing data.");
        }
    }

    private static void WriteArraySection<T>(
        FileStream file,
        ICollection<SectionDescriptor> descriptors,
        SectionType type,
        T[] values)
        where T : unmanaged
    {
        WriteSection(file, descriptors, type, values.LongLength, Marshal.SizeOf<T>(), writer =>
            writer.WriteUnmanaged(values));
    }

    private static void WriteByteSection(
        FileStream file,
        ICollection<SectionDescriptor> descriptors,
        SectionType type,
        byte[] values)
    {
        WriteSection(file, descriptors, type, values.LongLength, sizeof(byte), writer => writer.Write(values));
    }

    private static void WriteSection(
        FileStream file,
        ICollection<SectionDescriptor> descriptors,
        SectionType type,
        long count,
        int elementSize,
        Action<SectionWriter> write)
    {
        var offset = file.Position;
        using var writer = new SectionWriter(file);
        write(writer);
        var length = file.Position - offset;
        descriptors.Add(new SectionDescriptor(
            type,
            RequiredSectionFlag,
            offset,
            length,
            count,
            elementSize,
            writer.CompleteHash()));
    }

    private static T[] ReadArray<T>(
        FileStream file,
        SectionDescriptor descriptor,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        var expectedSize = Marshal.SizeOf<T>();
        if (descriptor.ElementSize != expectedSize
            || descriptor.Length != checked(descriptor.Count * expectedSize))
        {
            throw new InvalidDataException($"LOSN section {descriptor.Type} has the wrong element type.");
        }

        var values = new T[checked((int)descriptor.Count)];
        file.Position = descriptor.Offset;
        var bytes = MemoryMarshal.AsBytes(values.AsSpan());
        ReadExactly(file, bytes, cancellationToken);
        return values;
    }

    private static byte[] ReadBytes(
        FileStream file,
        SectionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (descriptor.ElementSize != sizeof(byte) || descriptor.Length > int.MaxValue)
        {
            throw new InvalidDataException($"LOSN byte section {descriptor.Type} is too large or malformed.");
        }

        var values = new byte[checked((int)descriptor.Length)];
        file.Position = descriptor.Offset;
        ReadExactly(file, values, cancellationToken);
        return values;
    }

    private static NameTableCheckpointState[] ReadCheckpoints(
        FileStream file,
        SectionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        file.Position = descriptor.Offset;
        var end = checked(descriptor.Offset + descriptor.Length);
        var count = ReadInt32(file, end);
        if (count < 0 || count != descriptor.Count)
        {
            throw new InvalidDataException("LOSN checkpoint count mismatch.");
        }

        var checkpoints = new NameTableCheckpointState[count];
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkpoints[i] = new NameTableCheckpointState(
                ReadString(file, end),
                ReadString(file, end),
                ReadUInt64(file, end),
                ReadInt64(file, end),
                ReadInt64(file, end),
                ReadInt64(file, end));
        }

        if (file.Position != end)
        {
            throw new InvalidDataException("LOSN checkpoint section has trailing bytes.");
        }

        return checkpoints;
    }

    private static string ReadString(FileStream file, long sectionEnd)
    {
        var length = ReadInt32(file, sectionEnd);
        if (length < 0 || length > MaximumCheckpointStringBytes || file.Position > sectionEnd - length)
        {
            throw new InvalidDataException("LOSN checkpoint string bounds are invalid.");
        }

        var bytes = new byte[length];
        file.ReadExactly(bytes);
        return StrictUtf8.GetString(bytes);
    }

    private static int ReadInt32(FileStream file, long sectionEnd)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        ReadPrimitive(file, sectionEnd, bytes);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static long ReadInt64(FileStream file, long sectionEnd)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        ReadPrimitive(file, sectionEnd, bytes);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FileStream file, long sectionEnd)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ReadPrimitive(file, sectionEnd, bytes);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void ReadPrimitive(FileStream file, long sectionEnd, Span<byte> destination)
    {
        if (file.Position > sectionEnd - destination.Length)
        {
            throw new InvalidDataException("LOSN checkpoint primitive is truncated.");
        }

        file.ReadExactly(destination);
    }

    private static void ReadExactly(FileStream file, Span<byte> destination, CancellationToken cancellationToken)
    {
        const int chunkSize = 4 * 1024 * 1024;
        var cursor = 0;
        while (cursor < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(chunkSize, destination.Length - cursor);
            file.ReadExactly(destination.Slice(cursor, count));
            cursor += count;
        }
    }

    private sealed class SectionWriter : IDisposable
    {
        private readonly FileStream _file;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _completed;

        internal SectionWriter(FileStream file)
        {
            _file = file;
        }

        internal void Write(byte[] bytes) => Write(bytes.AsSpan());

        internal void Write(ReadOnlySpan<byte> bytes)
        {
            const int chunkSize = 4 * 1024 * 1024;
            var cursor = 0;
            while (cursor < bytes.Length)
            {
                var count = Math.Min(chunkSize, bytes.Length - cursor);
                var chunk = bytes.Slice(cursor, count);
                _file.Write(chunk);
                _hash.AppendData(chunk);
                cursor += count;
            }
        }

        internal void WriteUnmanaged<T>(T[] values) where T : unmanaged =>
            Write(MemoryMarshal.AsBytes(values.AsSpan()));

        internal void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        internal void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            Write(bytes);
        }

        internal void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            Write(bytes);
        }

        internal void WriteString(string value)
        {
            var byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount > MaximumCheckpointStringBytes)
            {
                throw new InvalidDataException("LOSN checkpoint string is too long.");
            }

            WriteInt32(byteCount);
            Span<byte> bytes = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
            StrictUtf8.GetBytes(value, bytes);
            Write(bytes);
        }

        internal byte[] CompleteHash()
        {
            if (_completed)
            {
                throw new InvalidOperationException("The LOSN section hash was already completed.");
            }

            _completed = true;
            return _hash.GetHashAndReset();
        }

        public void Dispose() => _hash.Dispose();
    }

    private enum SectionType
    {
        Checkpoints = 1,
        Entries = 2,
        NameHeap = 3,
        NameOffsets = 4,
        NameOrder = 5,
        RootIds = 6,
        FrnOrder = 7,
        NameRecordOffsets = 8,
        NameRecordIds = 9,
        ChildOffsets = 10,
        ChildIds = 11,
        TrigramKeys = 12,
        TrigramByteOffsets = 13,
        TrigramPostingCounts = 14,
        TrigramPayload = 15,
        NameRecordIdsById = 18,
        ShortSearchNameIds = 19,
        ShortAliasKeys = 20,
        ShortAliasByteOffsets = 21,
        ShortAliasPostingCounts = 22,
        ShortAliasPayload = 23
    }

    private sealed record SectionDescriptor(
        SectionType Type,
        uint Flags,
        long Offset,
        long Length,
        long Count,
        int ElementSize,
        byte[] Hash);

    private sealed record SnapshotHeader(
        long FileLength,
        long Generation,
        long SectionTableOffset,
        int SectionCount,
        int CheckpointCount,
        long EntryCount,
        long LiveCount,
        long TombstoneCount,
        long NameCount,
        long TableLength,
        byte[] TableHash);

    private sealed record SnapshotLayout(
        long Generation,
        long EntryCount,
        long LiveCount,
        long TombstoneCount,
        long NameCount,
        IReadOnlyDictionary<SectionType, SectionDescriptor> Sections)
    {
        internal SectionDescriptor Required(SectionType type) =>
            Sections.TryGetValue(type, out var descriptor)
                ? descriptor
                : throw new InvalidDataException($"Required LOSN section {type} is absent.");
    }
}
