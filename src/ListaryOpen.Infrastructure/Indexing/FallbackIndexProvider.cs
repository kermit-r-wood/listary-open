using System.IO;
using System.Runtime.CompilerServices;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Infrastructure.Indexing;

public sealed class FallbackIndexProvider : IIndexProvider
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

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

            foreach (var directory in EnumerateDirectories(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                yield return FileRecord.Create(info.FullName, true, 0, new DateTimeOffset(info.LastWriteTimeUtc));
                pending.Push(info.FullName);
                await Task.Yield();
            }

            foreach (var file in EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                yield return FileRecord.Create(info.FullName, false, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
                await Task.Yield();
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", EnumerationOptions).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", EnumerationOptions).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
