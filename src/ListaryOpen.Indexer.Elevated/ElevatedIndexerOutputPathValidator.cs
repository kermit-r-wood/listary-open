namespace ListaryOpen.Indexer.Elevated;

internal static class ElevatedIndexerOutputPathValidator
{
    private const string FileNamePrefix = "listary-open-indexer-";

    public static bool AreAllowed(string recordsPath, string errorPath)
    {
        return IsAllowed(recordsPath, ".jsonl") && IsAllowed(errorPath, ".err");
    }

    public static bool IsAllowedErrorPath(string errorPath)
    {
        return IsAllowed(errorPath, ".err");
    }

    private static bool IsAllowed(string path, string extension)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (!tempPath.EndsWith(Path.DirectorySeparatorChar))
        {
            tempPath += Path.DirectorySeparatorChar;
        }

        var fileName = Path.GetFileName(fullPath);
        return fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase)
            && fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(fileName), extension, StringComparison.OrdinalIgnoreCase);
    }
}
