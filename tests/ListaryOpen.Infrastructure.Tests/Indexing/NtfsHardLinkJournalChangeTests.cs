using ListaryOpen.Indexer.Elevated.Ntfs;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class NtfsHardLinkJournalChangeTests
{
    [Fact]
    public void HardLinkChangeReasonConstantMatchesWindowsValue()
    {
        // USN_REASON_HARD_LINK_CHANGE
        Assert.Equal(0x00010000u, GetPrivateReasonConstant("UsnReasonHardLinkChange"));
    }

    [Fact]
    public void CreateHardLinkJournalChangesResyncsAndDeletesUsnPathWhenRecordGone()
    {
        var scanRoot = NtfsScanRoot.Create(@"C:\Docs");
        var directories = new Dictionary<ulong, NtfsUsnEntry>
        {
            [5] = new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
            [10] = new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true)
        };
        var resolved = new Dictionary<ulong, string>();
        var entry = new NtfsUsnEntry(
            FileReferenceNumber: 11,
            ParentFileReferenceNumber: 10,
            Name: "a.txt",
            IsDirectory: false,
            Usn: 100,
            Reason: 0x0001_0000);

        // Invalid handle + null geometry => resolver fails.
        var changes = NtfsUsnJournalReader.CreateHardLinkJournalChanges(
                volumeHandle: IntPtr.Zero,
                volumeGeometry: null,
                scanRoot,
                entry,
                directories,
                resolved,
                volumeRootFileReferenceNumber: 5)
            .ToList();

        Assert.Equal(2, changes.Count);
        Assert.Equal(UsnJournalChangeKind.HardLinkResync, changes[0].Kind);
        Assert.Equal(11ul, changes[0].FileReferenceNumber); // segment form
        Assert.Empty(changes[0].LiveHardLinkRecords!);
        Assert.Equal(UsnJournalChangeKind.Delete, changes[1].Kind);
        Assert.Equal(@"C:\Docs\a.txt", changes[1].FullPath);
    }

    [Fact]
    public void CreateHardLinkJournalChangesRequestsRescanWhenPathAndRecordUnavailable()
    {
        var scanRoot = NtfsScanRoot.Create(@"C:\");
        var directories = new Dictionary<ulong, NtfsUsnEntry>();
        var resolved = new Dictionary<ulong, string>();
        var entry = new NtfsUsnEntry(
            FileReferenceNumber: 99,
            ParentFileReferenceNumber: 12345, // unknown parent
            Name: "missing.txt",
            IsDirectory: false,
            Reason: 0x0001_0000);

        var changes = NtfsUsnJournalReader.CreateHardLinkJournalChanges(
                IntPtr.Zero,
                volumeGeometry: null,
                scanRoot,
                entry,
                directories,
                resolved,
                volumeRootFileReferenceNumber: 5)
            .ToList();

        var rescan = Assert.Single(changes);
        Assert.Equal(UsnJournalChangeKind.DirectoryRenameOrMove, rescan.Kind);
    }

    private static uint GetPrivateReasonConstant(string name)
    {
        var field = typeof(NtfsUsnJournalReader).GetField(
            name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        return (uint)field!.GetValue(null)!;
    }
}
