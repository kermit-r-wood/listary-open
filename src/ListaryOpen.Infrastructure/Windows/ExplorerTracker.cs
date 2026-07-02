using System.IO;

namespace ListaryOpen.Infrastructure.Windows;

public sealed class ExplorerTracker
{
    private string? _lastFolder;

    public string? LastFolder => _lastFolder;

    public void ObserveFolderForTests(string folderPath)
    {
        if (Directory.Exists(folderPath))
        {
            _lastFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
        }
    }
}
