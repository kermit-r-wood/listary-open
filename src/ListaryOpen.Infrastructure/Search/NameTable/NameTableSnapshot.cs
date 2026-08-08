namespace ListaryOpen.Infrastructure.Search.NameTable;

internal sealed record NameTableCheckpointState(
    string VolumeRoot,
    string FileSystemName,
    ulong UsnJournalId,
    long NextUsn,
    long RulesVersion,
    long LastFullScanUnixMilliseconds);

/// <summary>
/// Self-contained immutable capture used by the streaming LOSN writer.  Capture
/// copies only primitive compact arrays; it never materializes FileRecord paths.
/// </summary>
internal sealed record NameTableCompactSnapshot(
    long Generation,
    int EntryCount,
    int LiveCount,
    int TombstoneCount,
    CompactFileEntry[] Entries,
    CompactNameStoreSnapshot Names,
    int[] NameOrder,
    int[] ShortSearchNameIds,
    int[] RootIds,
    int[] FrnOrder,
    int[] NameRecordOffsets,
    int[] NameRecordIds,
    int[] NameRecordIdsById,
    int[] ChildOffsets,
    int[] ChildIds,
    CompactTrigramSnapshot Trigrams,
    CompactTrigramSnapshot ShortAliases,
    NameTableCheckpointState[] Checkpoints);
