using System.Diagnostics;
using System.Windows;

namespace ListaryOpen.App.Search;

internal interface ISearchResultActivationService
{
    Task OpenAsync(string path);

    Task RevealAsync(string path, bool isDirectory);

    void CopyPath(string path);
}

internal sealed class SearchResultActivator : ISearchResultActivationService
{
    public Task OpenAsync(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }

    public Task RevealAsync(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return OpenAsync(path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\""
        });

        return Task.CompletedTask;
    }

    public void CopyPath(string path)
    {
        Clipboard.SetText(path);
    }
}
