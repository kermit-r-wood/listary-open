namespace ListaryOpen.Infrastructure.Indexing.Ntfs;

public sealed record UsnJournalState(ulong UsnJournalId, long LowestValidUsn, long NextUsn);

public enum UsnCatchUpAction
{
    None,
    ReadJournal,
    FullRescan
}

public sealed record UsnCatchUpPlan(UsnCatchUpAction Action, long StartUsn, long EndUsn);

public static class UsnJournalCatchUpPlanner
{
    public static UsnCatchUpPlan Plan(
        UsnJournalCheckpoint? checkpoint,
        UsnJournalState journal,
        long currentRulesVersion)
    {
        if (checkpoint is null
            || checkpoint.UsnJournalId != journal.UsnJournalId
            || checkpoint.RulesVersion != currentRulesVersion
            || checkpoint.NextUsn < journal.LowestValidUsn)
        {
            return new UsnCatchUpPlan(UsnCatchUpAction.FullRescan, 0, journal.NextUsn);
        }

        return checkpoint.NextUsn >= journal.NextUsn
            ? new UsnCatchUpPlan(UsnCatchUpAction.None, journal.NextUsn, journal.NextUsn)
            : new UsnCatchUpPlan(UsnCatchUpAction.ReadJournal, checkpoint.NextUsn, journal.NextUsn);
    }
}
