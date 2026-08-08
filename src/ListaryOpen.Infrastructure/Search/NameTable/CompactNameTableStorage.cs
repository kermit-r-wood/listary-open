using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Infrastructure.Search.NameTable;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CompactFileEntry
{
    internal const uint NoParent = 0x3FFF_FFFFu;
    private const uint ParentMask = 0x3FFF_FFFFu;
    private const uint DirectoryFlag = 0x4000_0000u;
    private const uint LiveFlag = 0x8000_0000u;

    internal uint ParentAndFlags;
    internal int NameId;
    internal long SizeBytes;
    internal long LastWriteUtcTicksAndFlags;
    internal ulong FileReferenceNumber;

    internal readonly int ParentId => (ParentAndFlags & ParentMask) == NoParent
        ? -1
        : (int)(ParentAndFlags & ParentMask);

    internal readonly bool IsDirectory => (ParentAndFlags & DirectoryFlag) != 0;

    internal readonly bool IsLive => (ParentAndFlags & LiveFlag) != 0;

    internal readonly long LastWriteUtcTicks => LastWriteUtcTicksAndFlags & long.MaxValue;

    internal readonly bool PathHasAlias => LastWriteUtcTicksAndFlags < 0;

    internal void SetParentAndFlags(int parentId, bool isDirectory, bool isLive)
    {
        if (parentId < -1 || parentId >= NoParent)
        {
            throw new ArgumentOutOfRangeException(nameof(parentId));
        }

        ParentAndFlags = parentId < 0 ? NoParent : (uint)parentId;
        if (isDirectory)
        {
            ParentAndFlags |= DirectoryFlag;
        }

        if (isLive)
        {
            ParentAndFlags |= LiveFlag;
        }
    }

    internal void MarkDeleted() => ParentAndFlags &= ~LiveFlag;

    internal void SetLastWriteAndPathAlias(long utcTicks, bool pathHasAlias)
    {
        if (utcTicks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(utcTicks));
        }

        LastWriteUtcTicksAndFlags = pathHasAlias ? utcTicks | long.MinValue : utcTicks;
    }
}

internal sealed class CompactNameStore
{
    private const int InitialHeapCapacity = 64 * 1024;
    private const int InitialNameCapacity = 4 * 1024;
    private const ushort FoldSharesOriginalFlag = 0x8000;
    private const ushort FoldLengthMask = 0x7FFF;
    private const int HeaderSize = 8;

    private byte[] _heap;
    private int _heapLength;
    private int[] _offsets;
    private int _count;

    internal CompactNameStore()
        : this(
            new byte[InitialHeapCapacity],
            heapLength: 0,
            new int[InitialNameCapacity],
            count: 0)
    {
    }

    private CompactNameStore(byte[] heap, int heapLength, int[] offsets, int count)
    {
        _heap = heap;
        _heapLength = heapLength;
        _offsets = offsets;
        _count = count;
    }

    internal int Count => _count;

    internal int OffsetCapacity => _offsets.Length;

    internal int HeapLength => _heapLength;

    internal long CapacityBytes => _heap.LongLength + (long)_offsets.LongLength * sizeof(int);

    internal int Add(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Windows name matching is case-insensitive.  Lowercase is deliberately
        // used here because real file-name corpora contain far more lowercase
        // names, so the folded spelling can frequently share the original bytes.
        var folded = name.ToLowerInvariant();
        var alias = PinyinMatcher.CreateSearchAliases(name);
        if (!string.IsNullOrEmpty(alias))
        {
            alias = alias.ToLowerInvariant();
        }

        var originalLength = Encoding.UTF8.GetByteCount(name);
        var foldedLength = Encoding.UTF8.GetByteCount(folded);
        var aliasLength = string.IsNullOrEmpty(alias) ? 0 : Encoding.UTF8.GetByteCount(alias);
        if (originalLength > ushort.MaxValue || foldedLength > FoldLengthMask || aliasLength > ushort.MaxValue)
        {
            throw new InvalidDataException("A file name or generated search alias is too long for the compact name table.");
        }

        var sharesFolded = string.Equals(name, folded, StringComparison.Ordinal);
        var storedFoldedLength = sharesFolded ? 0 : foldedLength;
        var totalLength = checked(HeaderSize + originalLength + storedFoldedLength + aliasLength);
        EnsureHeapCapacity(checked(_heapLength + totalLength));
        EnsureNameCapacity(_count + 1);

        var offset = _heapLength;
        var destination = _heap.AsSpan(offset, totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(destination, checked((ushort)originalLength));
        var foldedMetadata = checked((ushort)foldedLength);
        if (sharesFolded)
        {
            foldedMetadata |= FoldSharesOriginalFlag;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], foldedMetadata);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], checked((ushort)aliasLength));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], 0);

        var cursor = HeaderSize;
        cursor += Encoding.UTF8.GetBytes(name, destination[cursor..]);
        if (!sharesFolded)
        {
            cursor += Encoding.UTF8.GetBytes(folded, destination[cursor..]);
        }

        if (aliasLength > 0)
        {
            cursor += Encoding.UTF8.GetBytes(alias, destination[cursor..]);
        }

        if (cursor != totalLength)
        {
            throw new InvalidDataException("Compact name encoding length mismatch.");
        }

        _offsets[_count] = offset;
        _heapLength += totalLength;
        return _count++;
    }

    internal ReadOnlySpan<byte> GetOriginal(int nameId)
    {
        var blob = GetBlob(nameId);
        var originalLength = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        return blob.Slice(HeaderSize, originalLength);
    }

    internal ReadOnlySpan<byte> GetFolded(int nameId)
    {
        var blob = GetBlob(nameId);
        var originalLength = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        var foldedMetadata = BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]);
        var foldedLength = foldedMetadata & FoldLengthMask;
        return (foldedMetadata & FoldSharesOriginalFlag) != 0
            ? blob.Slice(HeaderSize, originalLength)
            : blob.Slice(HeaderSize + originalLength, foldedLength);
    }

    internal ReadOnlySpan<byte> GetAlias(int nameId)
    {
        var blob = GetBlob(nameId);
        var originalLength = BinaryPrimitives.ReadUInt16LittleEndian(blob);
        var foldedMetadata = BinaryPrimitives.ReadUInt16LittleEndian(blob[2..]);
        var foldedLength = foldedMetadata & FoldLengthMask;
        var aliasLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[4..]);
        var storedFoldedLength = (foldedMetadata & FoldSharesOriginalFlag) != 0 ? 0 : foldedLength;
        return blob.Slice(HeaderSize + originalLength + storedFoldedLength, aliasLength);
    }

    internal string MaterializeOriginal(int nameId) => Encoding.UTF8.GetString(GetOriginal(nameId));

    internal bool OriginalEquals(int nameId, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var original = GetOriginal(nameId);
        if (byteCount != original.Length)
        {
            return false;
        }

        Span<byte> stack = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(value, stack);
        return original.SequenceEqual(stack);
    }

    internal CompactNameStore Clone()
    {
        var heap = new byte[Math.Max(_heapLength, InitialHeapCapacity)];
        _heap.AsSpan(0, _heapLength).CopyTo(heap);
        var offsets = new int[Math.Max(_count, InitialNameCapacity)];
        _offsets.AsSpan(0, _count).CopyTo(offsets);
        return new CompactNameStore(heap, _heapLength, offsets, _count);
    }

    internal void TrimExcess()
    {
        if (_heap.Length != _heapLength)
        {
            Array.Resize(ref _heap, Math.Max(_heapLength, 1));
        }

        if (_offsets.Length != _count)
        {
            Array.Resize(ref _offsets, Math.Max(_count, 1));
        }
    }

    internal CompactNameStoreSnapshot Capture() => new(
        _heap,
        _heapLength,
        _offsets,
        _count);

    internal CompactNameStoreSnapshot CaptureCopy()
    {
        var heap = new byte[_heapLength];
        _heap.AsSpan(0, _heapLength).CopyTo(heap);
        var offsets = new int[_count];
        _offsets.AsSpan(0, _count).CopyTo(offsets);
        return new CompactNameStoreSnapshot(heap, _heapLength, offsets, _count);
    }

    internal static CompactNameStore Restore(byte[] heap, int heapLength, int[] offsets, int count)
    {
        ArgumentNullException.ThrowIfNull(heap);
        ArgumentNullException.ThrowIfNull(offsets);
        if (heapLength < 0 || heapLength > heap.Length || count < 0 || count > offsets.Length)
        {
            throw new InvalidDataException("Compact name store dimensions are invalid.");
        }

        var store = new CompactNameStore(heap, heapLength, offsets, count);
        for (var i = 0; i < count; i++)
        {
            _ = store.GetOriginal(i);
            _ = store.GetFolded(i);
            _ = store.GetAlias(i);
        }

        return store;
    }

    private ReadOnlySpan<byte> GetBlob(int nameId)
    {
        if ((uint)nameId >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(nameId));
        }

        var offset = _offsets[nameId];
        if (offset < 0 || offset > _heapLength - HeaderSize)
        {
            throw new InvalidDataException("Compact name offset is outside the heap.");
        }

        var header = _heap.AsSpan(offset, HeaderSize);
        var originalLength = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var foldedMetadata = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        var foldedLength = foldedMetadata & FoldLengthMask;
        var aliasLength = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var storedFoldedLength = (foldedMetadata & FoldSharesOriginalFlag) != 0 ? 0 : foldedLength;
        var blobLength = checked(HeaderSize + originalLength + storedFoldedLength + aliasLength);
        if (offset > _heapLength - blobLength)
        {
            throw new InvalidDataException("Compact name blob extends beyond the heap.");
        }

        return _heap.AsSpan(offset, blobLength);
    }

    private void EnsureHeapCapacity(int required)
    {
        if (required <= _heap.Length)
        {
            return;
        }

        var capacity = _heap.Length;
        while (capacity < required)
        {
            capacity = checked(capacity < 16 * 1024 * 1024
                ? capacity * 2
                : capacity + Math.Min(capacity / 2, 8 * 1024 * 1024));
        }

        Array.Resize(ref _heap, capacity);
    }

    private void EnsureNameCapacity(int required)
    {
        if (required <= _offsets.Length)
        {
            return;
        }

        var capacity = _offsets.Length;
        while (capacity < required)
        {
            capacity = checked(capacity < 1_000_000
                ? capacity * 2
                : capacity + Math.Min(capacity / 2, 65_536));
        }

        Array.Resize(ref _offsets, capacity);
    }
}

internal readonly record struct CompactNameStoreSnapshot(
    byte[] Heap,
    int HeapLength,
    int[] Offsets,
    int Count);

/// <summary>
/// Build-time only open-address index. It stores no managed key objects and is
/// released when immutable sorted orders are published.
/// </summary>
internal sealed class CompactHashLookup
{
    private const double MaximumLoadFactor = 0.68;
    private ulong[] _hashes = new ulong[1024];
    private int[] _idsPlusOne = new int[1024];
    private int _count;

    internal long CapacityBytes => _hashes.LongLength * sizeof(ulong) + _idsPlusOne.LongLength * sizeof(int);

    internal bool TryFind(ulong hash, Func<int, bool> exactMatch, out int id)
    {
        ArgumentNullException.ThrowIfNull(exactMatch);
        var mask = _hashes.Length - 1;
        var slot = (int)(Mix(hash) & (uint)mask);
        while (true)
        {
            var encoded = _idsPlusOne[slot];
            if (encoded == 0)
            {
                id = -1;
                return false;
            }

            if (_hashes[slot] == hash)
            {
                var candidate = encoded - 1;
                if (exactMatch(candidate))
                {
                    id = candidate;
                    return true;
                }
            }

            slot = (slot + 1) & mask;
        }
    }

    internal void Add(ulong hash, int id)
    {
        if ((_count + 1d) / _hashes.Length > MaximumLoadFactor)
        {
            Resize(_hashes.Length * 2);
        }

        Insert(hash, id);
    }

    internal CompactHashLookup Clone()
    {
        var clone = new CompactHashLookup
        {
            _hashes = (ulong[])_hashes.Clone(),
            _idsPlusOne = (int[])_idsPlusOne.Clone(),
            _count = _count
        };
        return clone;
    }

    private void Insert(ulong hash, int id)
    {
        var mask = _hashes.Length - 1;
        var slot = (int)(Mix(hash) & (uint)mask);
        while (_idsPlusOne[slot] != 0)
        {
            slot = (slot + 1) & mask;
        }

        _hashes[slot] = hash;
        _idsPlusOne[slot] = checked(id + 1);
        _count++;
    }

    private void Resize(int capacity)
    {
        var oldHashes = _hashes;
        var oldIds = _idsPlusOne;
        _hashes = new ulong[capacity];
        _idsPlusOne = new int[capacity];
        _count = 0;
        for (var i = 0; i < oldIds.Length; i++)
        {
            if (oldIds[i] != 0)
            {
                Insert(oldHashes[i], oldIds[i] - 1);
            }
        }
    }

    private static uint Mix(ulong value)
    {
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        value *= 0xc4ceb9fe1a85ec53UL;
        value ^= value >> 33;
        return (uint)(value ^ (value >> 32));
    }
}

/// <summary>
/// Immutable byte-trigram postings over the deduplicated folded-name and alias
/// heaps. Postings are positions in name_order, delta-varint encoded. One/two
/// byte queries use the sparse alias table plus prefix/fallback lanes instead of
/// retaining postings for every byte and byte-pair in every name.
/// </summary>
internal sealed class CompactTrigramIndex
{
    internal static readonly CompactTrigramIndex Empty = new(
        Array.Empty<uint>(),
        [0],
        Array.Empty<int>(),
        Array.Empty<byte>());

    private readonly uint[] _keys;
    private readonly int[] _byteOffsets;
    private readonly int[] _postingCounts;
    private readonly byte[] _payload;

    private CompactTrigramIndex(
        uint[] keys,
        int[] byteOffsets,
        int[] postingCounts,
        byte[] payload)
    {
        _keys = keys;
        _byteOffsets = byteOffsets;
        _postingCounts = postingCounts;
        _payload = payload;
    }

    internal long CapacityBytes =>
        ((long)_keys.LongLength + _byteOffsets.LongLength + _postingCounts.LongLength) * sizeof(int)
        + _payload.LongLength;

    internal CompactTrigramSnapshot CaptureCopy() => new(
        (uint[])_keys.Clone(),
        (int[])_byteOffsets.Clone(),
        (int[])_postingCounts.Clone(),
        (byte[])_payload.Clone());

    internal static CompactTrigramIndex Restore(CompactTrigramSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Keys);
        ArgumentNullException.ThrowIfNull(snapshot.ByteOffsets);
        ArgumentNullException.ThrowIfNull(snapshot.PostingCounts);
        ArgumentNullException.ThrowIfNull(snapshot.Payload);
        if (snapshot.ByteOffsets.Length != snapshot.Keys.Length + 1
            || snapshot.PostingCounts.Length != snapshot.Keys.Length
            || snapshot.ByteOffsets.Length == 0
            || snapshot.ByteOffsets[0] != 0
            || snapshot.ByteOffsets[^1] != snapshot.Payload.Length)
        {
            throw new InvalidDataException("Compact trigram dimensions are invalid.");
        }

        for (var i = 0; i < snapshot.Keys.Length; i++)
        {
            if (i > 0 && snapshot.Keys[i - 1] >= snapshot.Keys[i])
            {
                throw new InvalidDataException("Compact trigram keys are not strictly ordered.");
            }

            if (snapshot.PostingCounts[i] < 0
                || snapshot.ByteOffsets[i] > snapshot.ByteOffsets[i + 1])
            {
                throw new InvalidDataException("Compact trigram posting bounds are invalid.");
            }
        }

        return new CompactTrigramIndex(
            snapshot.Keys,
            snapshot.ByteOffsets,
            snapshot.PostingCounts,
            snapshot.Payload);
    }

    internal static CompactTrigramIndex Build(CompactNameStore names, int[] nameOrder)
        => Build(names, nameOrder, minimumGramSize: 3, maximumGramSize: 3);

    internal static CompactTrigramIndex BuildShortAliases(CompactNameStore names, int[] nameOrder)
        => Build(names, nameOrder, minimumGramSize: 1, maximumGramSize: 2);

    private static CompactTrigramIndex Build(
        CompactNameStore names,
        int[] nameOrder,
        int minimumGramSize,
        int maximumGramSize)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(nameOrder);
        if (nameOrder.Length == 0)
        {
            return Empty;
        }

        var counts = new Dictionary<uint, int>();
        var grams = new HashSet<uint>();
        foreach (var nameId in nameOrder)
        {
            grams.Clear();
            AddSearchGrams(names.GetFolded(nameId), minimumGramSize, maximumGramSize, grams);
            AddSearchGrams(names.GetAlias(nameId), minimumGramSize, maximumGramSize, grams);
            foreach (var gram in grams)
            {
                counts.TryGetValue(gram, out var count);
                counts[gram] = checked(count + 1);
            }
        }

        if (counts.Count == 0)
        {
            return Empty;
        }

        var keys = counts.Keys.ToArray();
        Array.Sort(keys);
        var postingCounts = new int[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            postingCounts[i] = counts[keys[i]];
            // Reuse the counting dictionary as gram -> slot after its counts
            // have been copied to the compact posting-count array.
            counts[keys[i]] = i;
        }

        // Determine each encoded posting length directly from monotonically
        // increasing name-order positions. This avoids the former O(total
        // postings) int[] (hundreds of MiB on a real multi-million-file corpus).
        var byteOffsets = new int[keys.Length + 1];
        var previousPositions = new int[keys.Length];
        for (var position = 0; position < nameOrder.Length; position++)
        {
            grams.Clear();
            var nameId = nameOrder[position];
            AddSearchGrams(names.GetFolded(nameId), minimumGramSize, maximumGramSize, grams);
            AddSearchGrams(names.GetAlias(nameId), minimumGramSize, maximumGramSize, grams);
            foreach (var gram in grams)
            {
                var slot = counts[gram];
                var delta = checked((uint)(position - previousPositions[slot]));
                byteOffsets[slot + 1] = checked(byteOffsets[slot + 1] + VarUIntLength(delta));
                previousPositions[slot] = position;
            }
        }

        for (var slot = 1; slot < byteOffsets.Length; slot++)
        {
            byteOffsets[slot] = checked(byteOffsets[slot] + byteOffsets[slot - 1]);
        }

        var payload = new byte[byteOffsets[^1]];
        var writeCursors = (int[])byteOffsets.Clone();
        Array.Clear(previousPositions);
        for (var position = 0; position < nameOrder.Length; position++)
        {
            grams.Clear();
            var nameId = nameOrder[position];
            AddSearchGrams(names.GetFolded(nameId), minimumGramSize, maximumGramSize, grams);
            AddSearchGrams(names.GetAlias(nameId), minimumGramSize, maximumGramSize, grams);
            foreach (var gram in grams)
            {
                var slot = counts[gram];
                var delta = checked((uint)(position - previousPositions[slot]));
                writeCursors[slot] += WriteVarUInt(payload.AsSpan(writeCursors[slot]), delta);
                previousPositions[slot] = position;
            }
        }

        for (var slot = 0; slot < keys.Length; slot++)
        {
            if (writeCursors[slot] != byteOffsets[slot + 1])
            {
                throw new InvalidDataException("Trigram posting encoding length mismatch.");
            }
        }

        return new CompactTrigramIndex(keys, byteOffsets, postingCounts, payload);
    }

    /// <summary>
    /// Returns false when none of the patterns has a trigram (short-query path).
    /// Returns true with an empty list when a required gram does not exist.
    /// Otherwise exposes the rarest posting list as a repeatable lazy decoder;
    /// callers stop decoding as soon as their bounded candidate lane is full.
    /// </summary>
    internal bool TryGetRarestCandidatePositions(
        IReadOnlyList<byte[]> patterns,
        out PostingList positions,
        int minimumPatternLength = 3,
        int maximumGramLength = 3)
    {
        var usedIndex = false;
        var rarestSlot = -1;
        var rarestCount = int.MaxValue;
        var uniqueQueryGrams = new HashSet<uint>();
        foreach (var pattern in patterns)
        {
            if (pattern.Length < minimumPatternLength)
            {
                continue;
            }

            usedIndex = true;
            uniqueQueryGrams.Clear();
            AddGrams(pattern, Math.Min(maximumGramLength, pattern.Length), uniqueQueryGrams);
            foreach (var gram in uniqueQueryGrams)
            {
                var slot = Array.BinarySearch(_keys, gram);
                if (slot < 0)
                {
                    positions = PostingList.Empty;
                    return true;
                }

                if (_postingCounts[slot] < rarestCount)
                {
                    rarestCount = _postingCounts[slot];
                    rarestSlot = slot;
                }
            }
        }

        if (!usedIndex)
        {
            positions = PostingList.Empty;
            return false;
        }

        if (rarestSlot < 0 || rarestCount == 0)
        {
            positions = PostingList.Empty;
            return true;
        }

        positions = new PostingList(
            _payload,
            _byteOffsets[rarestSlot],
            _byteOffsets[rarestSlot + 1],
            rarestCount);
        return true;
    }

    internal readonly struct PostingList : IEnumerable<int>
    {
        internal static PostingList Empty { get; } = new(Array.Empty<byte>(), 0, 0, 0);

        private readonly byte[] _payload;
        private readonly int _start;
        private readonly int _end;
        private readonly int _count;

        internal PostingList(byte[] payload, int start, int end, int count)
        {
            _payload = payload;
            _start = start;
            _end = end;
            _count = count;
        }

        public Enumerator GetEnumerator() => new(_payload, _start, _end, _count);

        IEnumerator<int> IEnumerable<int>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<int>
        {
            private readonly byte[] _payload;
            private readonly int _end;
            private int _cursor;
            private int _remaining;
            private int _previous;

            internal Enumerator(byte[] payload, int start, int end, int count)
            {
                _payload = payload;
                _cursor = start;
                _end = end;
                _remaining = count;
                _previous = 0;
                Current = 0;
            }

            public int Current { get; private set; }

            readonly object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_remaining == 0)
                {
                    if (_cursor != _end)
                    {
                        throw new InvalidDataException("Trigram posting payload has trailing bytes.");
                    }

                    return false;
                }

                var delta = ReadVarUInt(_payload, ref _cursor, _end);
                _previous = checked(_previous + (int)delta);
                Current = _previous;
                _remaining--;
                return true;
            }

            public readonly void Dispose()
            {
            }

            public readonly void Reset() => throw new NotSupportedException();
        }
    }

    private static void AddSearchGrams(
        ReadOnlySpan<byte> value,
        int minimumGramSize,
        int maximumGramSize,
        HashSet<uint> destination)
    {
        for (var size = minimumGramSize; size <= Math.Min(maximumGramSize, value.Length); size++)
        {
            AddGrams(value, size, destination);
        }
    }

    private static void AddGrams(ReadOnlySpan<byte> value, int size, HashSet<uint> destination)
    {
        for (var i = 0; i <= value.Length - size; i++)
        {
            uint gram = (uint)size << 24;
            for (var j = 0; j < size; j++)
            {
                gram |= (uint)value[i + j] << ((size - j - 1) * 8);
            }

            destination.Add(gram);
        }
    }

    private static int VarUIntLength(uint value)
    {
        var length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }

        return length;
    }

    private static int WriteVarUInt(Span<byte> destination, uint value)
    {
        var cursor = 0;
        while (value >= 0x80)
        {
            destination[cursor++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[cursor++] = (byte)value;
        return cursor;
    }

    private static uint ReadVarUInt(byte[] payload, ref int cursor, int end)
    {
        uint value = 0;
        var shift = 0;
        while (cursor < end && shift <= 28)
        {
            var current = payload[cursor++];
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException("Invalid trigram posting varint.");
    }
}

internal sealed record CompactTrigramSnapshot(
    uint[] Keys,
    int[] ByteOffsets,
    int[] PostingCounts,
    byte[] Payload);

public readonly record struct NameTableMemoryStats(
    int LiveRecords,
    int RecordCapacity,
    int UniqueNames,
    int NameCapacity,
    long NameHeapBytes,
    long SortedIndexBytes,
    long BuildIndexBytes,
    long EstimatedRetainedBytes)
{
    public double BytesPerLiveRecord => LiveRecords == 0 ? 0 : (double)EstimatedRetainedBytes / LiveRecords;
}
