using System.IO;
using System.Runtime.CompilerServices;
using System.Security;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class FallbackIndexProvider : IIndexProvider
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    private readonly IndexExclusionRules _exclusionRules;

    public FallbackIndexProvider()
        : this(IndexExclusionRules.Default)
    {
    }

    public FallbackIndexProvider(IndexExclusionRules exclusionRules)
    {
        _exclusionRules = exclusionRules ?? throw new ArgumentNullException(nameof(exclusionRules));
    }

    public string Name => "Fallback";

    public bool CanIndex(VolumeInfo volume) => volume.IsReady;

    public async IAsyncEnumerable<FileRecord> ScanAsync(
        IndexRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root.Path);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();

            foreach (var directory in EnumerateDirectories(current, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_exclusionRules.ShouldExcludeDirectoryPath(directory))
                {
                    continue;
                }

                if (!TryCreateDirectoryRecord(directory, out var record, out var fullName))
                {
                    continue;
                }

                yield return record;
                pending.Push(fullName);
                await Task.Yield();
            }

            foreach (var file in EnumerateFiles(current, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryCreateFileRecord(file, out var record))
                {
                    continue;
                }

                yield return record;
                await Task.Yield();
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path, CancellationToken cancellationToken)
        => EnumerateEntries(() => Directory.EnumerateDirectories(path, "*", EnumerationOptions), cancellationToken);

    private static IEnumerable<string> EnumerateFiles(string path, CancellationToken cancellationToken)
        => EnumerateEntries(() => Directory.EnumerateFiles(path, "*", EnumerationOptions), cancellationToken);

    private static IEnumerable<string> EnumerateEntries(
        Func<IEnumerable<string>> enumerate,
        CancellationToken cancellationToken)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = enumerate().GetEnumerator();
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string current;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception exception) when (IsExpectedFileSystemException(exception))
                {
                    yield break;
                }

                yield return current;
            }
        }
    }

    private static bool TryCreateDirectoryRecord(string path, out FileRecord record, out string fullName)
    {
        record = null!;
        fullName = string.Empty;

        try
        {
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            fullName = info.FullName;
            record = FileRecord.Create(fullName, true, 0, new DateTimeOffset(info.LastWriteTimeUtc));
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return false;
        }
    }

    private static bool TryCreateFileRecord(string path, out FileRecord record)
    {
        record = null!;

        try
        {
            var info = new FileInfo(path);
            record = FileRecord.Create(info.FullName, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
            return true;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            return false;
        }
    }

    private static bool IsExpectedFileSystemException(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or SecurityException;
    }
}
