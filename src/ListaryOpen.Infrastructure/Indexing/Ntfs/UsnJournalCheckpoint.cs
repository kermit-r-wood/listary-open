namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

public sealed record UsnJournalCheckpoint(
    string VolumeRoot,
    string FileSystemName,
    ulong UsnJournalId,
    long NextUsn,
    long RulesVersion,
    DateTimeOffset LastFullScanAt);
