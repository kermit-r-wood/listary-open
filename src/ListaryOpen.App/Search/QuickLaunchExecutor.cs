using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ListaryOpen.Core.Settings;

namespace ListaryOpen.App.Search;

internal interface IQuickLaunchExecutor
{
    Task ExecuteAsync(QuickLaunchEntry entry);
}

internal sealed class QuickLaunchExecutor : IQuickLaunchExecutor
{
    public Task ExecuteAsync(QuickLaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = Environment.ExpandEnvironmentVariables(entry.Path);
        var workingDirectory = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
            : Environment.ExpandEnvironmentVariables(entry.WorkingDirectory);
        Process.Start(CreateStartInfo(entry, path, workingDirectory));
        return Task.CompletedTask;
    }

    internal static ProcessStartInfo CreateStartInfo(
        QuickLaunchEntry entry,
        string expandedPath,
        string expandedWorkingDirectory) => new()
    {
        FileName = expandedPath,
        Arguments = Environment.ExpandEnvironmentVariables(entry.Arguments),
        WorkingDirectory = expandedWorkingDirectory,
        UseShellExecute = true,
        Verb = entry.RunAsAdmin ? "runas" : string.Empty,
        WindowStyle = entry.Silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
    };
}
