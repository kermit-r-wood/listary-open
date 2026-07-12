using ListaryOpen.Indexer.Elevated.Ntfs;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class NtfsUsnRecordProjectorTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScanRootCreatePreservesRequestedRootSeparatelyFromVolumeRoot()
    {
        var root = NtfsScanRoot.Create("C:\\Docs");

        Assert.Equal("C:\\Docs", root.RequestedRoot);
        Assert.Equal("C:\\", root.VolumeRoot);
        Assert.Equal(@"\\.\C:", root.VolumePath);
    }

    [Fact]
    public void CreateFileRecordsIncludesRequestedRootItself()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\", isDirectory: true, sizeBytes: 0, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\",
            metadata,
            new NtfsUsnEntry(5, 5, ".", IsDirectory: true));

        var record = Assert.Single(records);
        Assert.Equal("C:\\", record.FullPath);
        Assert.True(record.IsDirectory);
    }

    [Fact]
    public void CreateFileRecordsIncludesChildUnderRequestedRoot()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Docs", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Docs\\Invoice.txt", isDirectory: false, sizeBytes: 42, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\Docs",
            metadata,
            new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
            new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true),
            new NtfsUsnEntry(11, 10, "Invoice.txt", IsDirectory: false));

        Assert.Collection(
            records,
            record => Assert.Equal("C:\\Docs", record.FullPath),
            record =>
            {
                Assert.Equal("C:\\Docs\\Invoice.txt", record.FullPath);
                Assert.False(record.IsDirectory);
                Assert.Equal(42, record.SizeBytes);
            });
    }

    [Fact]
    public void CreateFileRecordsExcludesSiblingPrefix()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Docs", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Docs2", isDirectory: true, sizeBytes: 0, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\Docs",
            metadata,
            new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
            new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true),
            new NtfsUsnEntry(20, 5, "Docs2", IsDirectory: true));

        var record = Assert.Single(records);
        Assert.Equal("C:\\Docs", record.FullPath);
    }

    [Fact]
    public void CreateFileRecordsMatchesRequestedRootCaseInsensitively()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Docs", isDirectory: true, sizeBytes: 0, Timestamp);

        var records = Project(
            "C:\\",
            "c:\\docs",
            metadata,
            new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
            new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true));

        var record = Assert.Single(records);
        Assert.Equal("C:\\Docs", record.FullPath);
    }

    [Fact]
    public void CreateFileRecordsSkipsUnresolvedParent()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Missing\\Invoice.txt", isDirectory: false, sizeBytes: 42, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\Missing",
            metadata,
            new NtfsUsnEntry(11, 10, "Invoice.txt", IsDirectory: false));

        Assert.Empty(records);
    }

    [Fact]
    public void CreateFileRecordsThrowsInStrictModeWhenParentCannotBeResolved()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Missing\\Invoice.txt", isDirectory: false, sizeBytes: 42, Timestamp);

        Assert.Throws<InvalidDataException>(() =>
            NtfsUsnRecordProjector
                .CreateFileRecords(
                    "C:\\",
                    "C:\\Missing",
                    [new NtfsUsnEntry(11, 10, "Invoice.txt", IsDirectory: false)],
                    metadata,
                    CancellationToken.None,
                    failOnSkippedRecords: true)
                .ToList());
    }

    [Fact]
    public void CreateFileRecordsSkipsUnresolvedNtfsMetadataInStrictMode()
    {
        var records = NtfsUsnRecordProjector
            .CreateFileRecords(
                "C:\\",
                "C:\\Users\\paulx",
                [new NtfsUsnEntry(0x0001_0000_0000_001B, 0x000B_0000_0000_000B, "$RmMetadata", IsDirectory: true)],
                new StubMetadataReader(),
                CancellationToken.None,
                volumeRootFileReferenceNumber: 5,
                failOnSkippedRecords: true)
            .ToList();

        Assert.Empty(records);
    }

    [Fact]
    public void CreateFileRecordsResolvesChildrenWhenVolumeRootRecordIsAbsent()
    {
        const ulong volumeRootFileReferenceNumber = 5;
        const ulong volumeRootRecordReferenceNumber = 0x0001_0000_0000_0005;

        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Users\\paulx", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Users\\paulx\\Invoice.txt", isDirectory: false, sizeBytes: 42, Timestamp);

        var records = NtfsUsnRecordProjector
            .CreateFileRecords(
                "C:\\",
                "C:\\Users\\paulx",
                [
                    new NtfsUsnEntry(10, volumeRootRecordReferenceNumber, "Users", IsDirectory: true),
                    new NtfsUsnEntry(11, 10, "paulx", IsDirectory: true),
                    new NtfsUsnEntry(12, 11, "Invoice.txt", IsDirectory: false)
                ],
                metadata,
                CancellationToken.None,
                volumeRootFileReferenceNumber: volumeRootFileReferenceNumber)
            .ToList();

        Assert.Collection(
            records,
            record => Assert.Equal("C:\\Users\\paulx", record.FullPath),
            record => Assert.Equal("C:\\Users\\paulx\\Invoice.txt", record.FullPath));
    }

    [Fact]
    public void CreateFileRecordsResolvesParentWhenSequenceNumberDiffers()
    {
        const ulong parentRecord = 0x0001_0000_0000_001B;
        const ulong parentReferenceFromChild = 0x0002_0000_0000_001B;
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Users", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Users\\profile.txt", isDirectory: false, sizeBytes: 7, Timestamp);

        var records = NtfsUsnRecordProjector
            .CreateFileRecords(
                "C:\\",
                "C:\\Users",
                [
                    new NtfsUsnEntry(parentRecord, 5, "Users", IsDirectory: true),
                    new NtfsUsnEntry(30, parentReferenceFromChild, "profile.txt", IsDirectory: false)
                ],
                metadata,
                CancellationToken.None,
                volumeRootFileReferenceNumber: 5,
                failOnSkippedRecords: true)
            .ToList();

        Assert.Collection(
            records,
            record => Assert.Equal("C:\\Users", record.FullPath),
            record => Assert.Equal("C:\\Users\\profile.txt", record.FullPath));
    }

    [Fact]
    public void CreateFileRecordsSkipsExcludedDirectorySubtrees()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Projects\\keep", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Projects\\keep\\visible.txt", isDirectory: false, sizeBytes: 7, Timestamp);
        metadata.Add("C:\\Projects\\node_modules", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Projects\\node_modules\\hidden.txt", isDirectory: false, sizeBytes: 6, Timestamp);

        var records = NtfsUsnRecordProjector
            .CreateFileRecords(
                "C:\\",
                "C:\\Projects",
                [
                    new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
                    new NtfsUsnEntry(10, 5, "Projects", IsDirectory: true),
                    new NtfsUsnEntry(11, 10, "keep", IsDirectory: true),
                    new NtfsUsnEntry(12, 11, "visible.txt", IsDirectory: false),
                    new NtfsUsnEntry(20, 10, "node_modules", IsDirectory: true),
                    new NtfsUsnEntry(21, 20, "hidden.txt", IsDirectory: false)
                ],
                metadata,
                CancellationToken.None,
                exclusionRules: IndexExclusionRules.Default)
            .ToList();

        Assert.Contains(records, record => record.FullPath.EndsWith("visible.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(records, record => record.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateFileRecordsSkipsCycles()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\CycleA\\CycleB", isDirectory: true, sizeBytes: 0, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\CycleA",
            metadata,
            new NtfsUsnEntry(10, 11, "CycleA", IsDirectory: true),
            new NtfsUsnEntry(11, 10, "CycleB", IsDirectory: true));

        Assert.Empty(records);
    }

    [Fact]
    public void CreateFileRecordsResolvesOutOfOrderRecords()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Docs", isDirectory: true, sizeBytes: 0, Timestamp);
        metadata.Add("C:\\Docs\\Invoice.txt", isDirectory: false, sizeBytes: 42, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\Docs",
            metadata,
            new NtfsUsnEntry(11, 10, "Invoice.txt", IsDirectory: false),
            new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true),
            new NtfsUsnEntry(5, 5, ".", IsDirectory: true));

        Assert.Collection(
            records,
            record => Assert.Equal("C:\\Docs\\Invoice.txt", record.FullPath),
            record => Assert.Equal("C:\\Docs", record.FullPath));
    }

    [Fact]
    public void CreateFileRecordsSkipsRecordsWhenMetadataCannotBeRead()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Docs", isDirectory: true, sizeBytes: 0, Timestamp);

        var records = Project(
            "C:\\",
            "C:\\Docs",
            metadata,
            new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
            new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true),
            new NtfsUsnEntry(11, 10, "Missing.txt", IsDirectory: false));

        var record = Assert.Single(records);
        Assert.Equal("C:\\Docs", record.FullPath);
    }

    [Fact]
    public void CreateFileRecordsThrowsInStrictModeWhenMetadataCannotBeRead()
    {
        var metadata = new StubMetadataReader();
        metadata.Add("C:\\Docs", isDirectory: true, sizeBytes: 0, Timestamp);

        Assert.Throws<IOException>(() =>
            NtfsUsnRecordProjector
                .CreateFileRecords(
                    "C:\\",
                    "C:\\Docs",
                    [
                        new NtfsUsnEntry(5, 5, ".", IsDirectory: true),
                        new NtfsUsnEntry(10, 5, "Docs", IsDirectory: true),
                        new NtfsUsnEntry(11, 10, "Missing.txt", IsDirectory: false)
                    ],
                    metadata,
                    CancellationToken.None,
                    failOnSkippedRecords: true)
                .ToList());
    }

    [Fact]
    public void NtfsFileMetadataReaderReadsRealFileMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("listary-open-ntfs-reader-");
        try
        {
            var filePath = Path.Combine(directory.FullName, "Invoice.txt");
            File.WriteAllText(filePath, "hello");
            var lastWriteTime = new DateTimeOffset(2026, 7, 3, 1, 2, 3, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(filePath, lastWriteTime.UtcDateTime);
            var reader = new NtfsFileMetadataReader();

            var success = reader.TryRead(filePath, isDirectory: false, CancellationToken.None, out var metadata);

            Assert.True(success);
            Assert.Equal(5, metadata.SizeBytes);
            Assert.Equal(lastWriteTime, metadata.LastWriteTime);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static List<ListaryOpen.Core.Indexing.FileRecord> Project(
        string volumeRoot,
        string requestedRoot,
        INtfsFileMetadataReader metadataReader,
        params NtfsUsnEntry[] entries)
    {
        return NtfsUsnRecordProjector
            .CreateFileRecords(volumeRoot, requestedRoot, entries, metadataReader, CancellationToken.None)
            .ToList();
    }

    private sealed class StubMetadataReader : INtfsFileMetadataReader
    {
        private readonly Dictionary<string, NtfsFileMetadata> _metadata = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string fullPath, bool isDirectory, long sizeBytes, DateTimeOffset lastWriteTime)
        {
            _metadata[Path.TrimEndingDirectorySeparator(fullPath)] = new NtfsFileMetadata(sizeBytes, lastWriteTime);
        }

        public bool TryRead(
            string fullPath,
            bool isDirectory,
            CancellationToken cancellationToken,
            out NtfsFileMetadata metadata)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_metadata.TryGetValue(Path.TrimEndingDirectorySeparator(fullPath), out var storedMetadata))
            {
                metadata = storedMetadata;
                return true;
            }

            metadata = default!;
            return false;
        }
    }
}
