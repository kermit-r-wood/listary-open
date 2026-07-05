namespace ListaryOpen.Indexer.Elevated;

internal static class ElevatedIndexerOutputPathValidator
{
    private const string FileNamePrefix = "listary-open-indexer-";

    public static bool AreAllowed(string recordsPath, string errorPath)
    {
        return IsAllowed(recordsPath, ".jsonl")
            && IsAllowed(errorPath, ".err");
    }

    public static bool IsAllowedErrorPath(string errorPath)
    {
        return IsAllowed(errorPath, ".err");
    }

    public static FileStream CreateNewFile(string path, string extension)
    {
        if (!IsAllowed(path, extension))
        {
            throw new InvalidOperationException("Elevated indexer output path is not allowed.");
        }

        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
    }

    private static bool IsAllowed(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var parentPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var tempPath = Path.TrimEndingDirectorySeparator(GetTrustedTempDirectory());
            parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
            if (!string.Equals(parentPath, tempPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IsReparsePointDirectory(tempPath) || PathExists(fullPath))
            {
                return false;
            }

            var fileName = Path.GetFileName(fullPath);
            return fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(fileName), extension, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or NotSupportedException
                                          or PathTooLongException
                                          or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string GetTrustedTempDirectory()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "tmp"));
    }

    private static bool IsReparsePointDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
