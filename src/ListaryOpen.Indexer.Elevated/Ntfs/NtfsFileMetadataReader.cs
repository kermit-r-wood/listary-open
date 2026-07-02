namespace ListaryOpen.Indexer.Elevated.Ntfs;

internal sealed record NtfsFileMetadata(long SizeBytes, DateTimeOffset LastWriteTime);

internal interface INtfsFileMetadataReader
{
    bool TryRead(
        string fullPath,
        bool isDirectory,
        CancellationToken cancellationToken,
        out NtfsFileMetadata metadata);
}

internal sealed class NtfsFileMetadataReader : INtfsFileMetadataReader
{
    public bool TryRead(
        string fullPath,
        bool isDirectory,
        CancellationToken cancellationToken,
        out NtfsFileMetadata metadata)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (isDirectory)
            {
                var directory = new DirectoryInfo(fullPath);
                if (!directory.Exists)
                {
                    metadata = default!;
                    return false;
                }

                metadata = new NtfsFileMetadata(0, new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero));
                return true;
            }

            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                metadata = default!;
                return false;
            }

            metadata = new NtfsFileMetadata(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            return true;
        }
        catch (Exception exception) when (IsExpectedMetadataFailure(exception))
        {
            metadata = default!;
            return false;
        }
    }

    private static bool IsExpectedMetadataFailure(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException;
    }
}
