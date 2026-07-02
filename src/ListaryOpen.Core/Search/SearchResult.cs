using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Core.Search;

public sealed record SearchResult(FileRecord Record, double Score, string MatchReason);
