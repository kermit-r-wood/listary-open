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
        IgnoreInaccessible = false,
        RecurseSubdirectories = false
    };

    private static readonly EnumerationOptions RootProbeEnumerationOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
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

    internal static bool RootProbeIgnoresInaccessibleForTests => RootProbeEnumerationOptions.IgnoreInaccessible;

    public bool CanIndex(VolumeInfo volume) => volume.IsReady;

    public async IAsyncEnumerable<FileRecord> ScanAsync(
        IndexRoot root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        EnsureRootCanBeEnumerated(root.Path, cancellationToken);

        var pending = new Stack<string>();
        pending.Push(root.Path);
        var normalizedRootPath = NormalizePath(root.Path);
        var skippedInaccessibleLocation = false;

        // The NTFS provider projects the requested directory itself, whereas the
        // compatibility walk historically started at its children. That made a
        // custom indexed root impossible to find by its own name while descendants
        // could still match through the parent path.
        if (TryCreateDirectoryRecord(root.Path, out var rootRecord, out _))
        {
            yield return rootRecord;
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = pending.Pop();
            var isRootEnumeration = string.Equals(
                NormalizePath(current),
                normalizedRootPath,
                StringComparison.OrdinalIgnoreCase);

            foreach (var directory in EnumerateDirectories(
                         current,
                         cancellationToken,
                         failOnEnumerationFailure: isRootEnumeration,
                         _ => skippedInaccessibleLocation = true))
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
            }

            foreach (var file in EnumerateFiles(
                         current,
                         cancellationToken,
                         failOnEnumerationFailure: isRootEnumeration,
                         _ => skippedInaccessibleLocation = true))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryCreateFileRecord(file, out var record))
                {
                    continue;
                }

                yield return record;
            }
        }

        if (skippedInaccessibleLocation)
        {
            throw new IOException(
                $"Index root could not be completely enumerated because one or more child locations were inaccessible: {root.Path}");
        }
    }

    internal static IEnumerable<string> EnumerateEntriesForTests(
        Func<IEnumerable<string>> enumerate,
        CancellationToken cancellationToken,
        bool failOnEnumerationFailure,
        Action<Exception>? onEnumerationFailure = null)
        => EnumerateEntries(enumerate, cancellationToken, failOnEnumerationFailure, onEnumerationFailure);

    private static IEnumerable<string> EnumerateDirectories(
        string path,
        CancellationToken cancellationToken,
        bool failOnEnumerationFailure,
        Action<Exception>? onEnumerationFailure)
        => EnumerateEntries(
            () => Directory.EnumerateDirectories(
                path,
                "*",
                failOnEnumerationFailure ? RootProbeEnumerationOptions : EnumerationOptions),
            cancellationToken,
            failOnEnumerationFailure,
            onEnumerationFailure);

    private static IEnumerable<string> EnumerateFiles(
        string path,
        CancellationToken cancellationToken,
        bool failOnEnumerationFailure,
        Action<Exception>? onEnumerationFailure)
        => EnumerateEntries(
            () => Directory.EnumerateFiles(
                path,
                "*",
                failOnEnumerationFailure ? RootProbeEnumerationOptions : EnumerationOptions),
            cancellationToken,
            failOnEnumerationFailure,
            onEnumerationFailure);

    private static void EnsureRootCanBeEnumerated(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Index root does not exist: {path}");
        }

        try
        {
            using var enumerator = Directory
                .EnumerateFileSystemEntries(path, "*", RootProbeEnumerationOptions)
                .GetEnumerator();
            _ = enumerator.MoveNext();
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            throw new IOException($"Index root could not be enumerated: {path}", exception);
        }
    }

    private static IEnumerable<string> EnumerateEntries(
        Func<IEnumerable<string>> enumerate,
        CancellationToken cancellationToken,
        bool failOnEnumerationFailure,
        Action<Exception>? onEnumerationFailure = null)
    {
        IEnumerator<string> enumerator;
        try
        {
            enumerator = enumerate().GetEnumerator();
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            if (failOnEnumerationFailure)
            {
                throw new IOException("Index root could not be enumerated.", exception);
            }

            onEnumerationFailure?.Invoke(exception);
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
                    if (failOnEnumerationFailure)
                    {
                        throw new IOException("Index root could not be enumerated.", exception);
                    }

                    onEnumerationFailure?.Invoke(exception);
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
            var attributes = info.Attributes;
            if (ShouldSkipReparseDirectory(attributes, GetLinkTargetWithoutTraversal(info)))
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

    internal static bool ShouldSkipReparseDirectory(FileAttributes attributes, string? linkTarget)
    {
        // OneDrive and other Cloud Files providers mark placeholder directories as
        // reparse points even though they are not links. Skipping every reparse
        // directory therefore makes entire online-only trees disappear. Only skip
        // reparse points that actually redirect traversal (junctions/symlinks);
        // this still prevents cycles without hydrating placeholder file contents.
        return attributes.HasFlag(FileAttributes.ReparsePoint) &&
            !string.IsNullOrWhiteSpace(linkTarget);
    }

    private static string? GetLinkTargetWithoutTraversal(DirectoryInfo info)
    {
        try
        {
            return info.LinkTarget;
        }
        catch (Exception exception) when (IsExpectedFileSystemException(exception))
        {
            // If the target cannot be classified, preserve the previous cycle-safe
            // behavior instead of risking traversal through a junction loop.
            return "<unavailable>";
        }
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
