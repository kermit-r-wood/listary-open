using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class UsnJournalCatchUpPlannerTests
{
    [Fact]
    public void PlanRequestsFullRescanWhenCheckpointIsMissing()
    {
        var journal = new UsnJournalState(1, LowestValidUsn: 50, NextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(null, journal, currentRulesVersion: 2);

        Assert.Equal(UsnCatchUpAction.FullRescan, plan.Action);
        Assert.Equal(0, plan.StartUsn);
        Assert.Equal(200, plan.EndUsn);
    }

    [Fact]
    public void PlanRequestsFullRescanWhenJournalIdChanges()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 100, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(2, LowestValidUsn: 50, NextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal, currentRulesVersion: 2);

        Assert.Equal(UsnCatchUpAction.FullRescan, plan.Action);
    }

    [Fact]
    public void PlanRequestsFullRescanWhenCheckpointIsBeforeLowestValidUsn()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 40, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(1, LowestValidUsn: 50, NextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal, currentRulesVersion: 2);

        Assert.Equal(UsnCatchUpAction.FullRescan, plan.Action);
    }

    [Fact]
    public void PlanRequestsFullRescanWhenRulesVersionChanges()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 100, 1, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(1, LowestValidUsn: 50, NextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal, currentRulesVersion: 2);

        Assert.Equal(UsnCatchUpAction.FullRescan, plan.Action);
    }

    [Fact]
    public void PlanReadsJournalRangeWhenCheckpointIsValid()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 100, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(1, LowestValidUsn: 50, NextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal, currentRulesVersion: 2);

        Assert.Equal(UsnCatchUpAction.ReadJournal, plan.Action);
        Assert.Equal(100, plan.StartUsn);
        Assert.Equal(200, plan.EndUsn);
    }

    [Fact]
    public void PlanDoesNothingWhenCheckpointAlreadyReachedJournalTip()
    {
        var checkpoint = new UsnJournalCheckpoint("C:\\", "NTFS", 1, 200, 2, DateTimeOffset.UtcNow);
        var journal = new UsnJournalState(1, LowestValidUsn: 50, NextUsn: 200);

        var plan = UsnJournalCatchUpPlanner.Plan(checkpoint, journal, currentRulesVersion: 2);

        Assert.Equal(UsnCatchUpAction.None, plan.Action);
        Assert.Equal(200, plan.StartUsn);
        Assert.Equal(200, plan.EndUsn);
    }
}
