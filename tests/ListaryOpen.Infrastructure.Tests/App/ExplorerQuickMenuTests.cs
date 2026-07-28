using ListaryOpen.App;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Windows;
using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class ExplorerQuickMenuTests
{
    [Fact]
    public void OpenFolderSnapshotKeepsOfflineAndCloudPathsWithoutProbingThem()
    {
        var offlineUnc = @"\\offline-nas\archive";
        var cloudPlaceholder = @"C:\Users\Test\OneDrive\Online only";

        var snapshot = ExplorerQuickMenu.CreateFolderSnapshot(new[]
        {
            new QuickSwitchFolderCandidate(offlineUnc, "Explorer", new IntPtr(1), false),
            new QuickSwitchFolderCandidate(offlineUnc.ToUpperInvariant(), "Explorer", new IntPtr(2), true),
            new QuickSwitchFolderCandidate(cloudPlaceholder, "Explorer", new IntPtr(3), false),
            new QuickSwitchFolderCandidate(" ", "Explorer", new IntPtr(4), false)
        });

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(offlineUnc, snapshot[0].FolderPath);
        Assert.Equal(cloudPlaceholder, snapshot[1].FolderPath);
    }

    [Fact]
    public void PowerShellStartsInTheExplorerFolder()
    {
        var folder = Path.GetFullPath("C:\\Projects");

        var startInfo = ExplorerQuickMenu.CreatePowerShellStartInfo(folder);

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.Equal(folder, startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
    }

    [Fact]
    public void CustomCommandExpandsConfiguredValuesAndExecutionOptions()
    {
        var entry = new QuickMenuEntry(
            "editor",
            "Edit here",
            QuickMenuAction.RunCommand,
            "%SystemRoot%\\System32\\notepad.exe",
            Arguments: "\"%CURRENT_FOLDER%\\notes.txt\"",
            WorkingDirectory: "%CURRENT_FOLDER%",
            Silent: true,
            RunAsAdmin: true);

        var startInfo = ExplorerQuickMenu.CreateConfiguredCommandStartInfo(entry, "C:\\Work");

        Assert.EndsWith("\\System32\\notepad.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("\"C:\\Work\\notes.txt\"", startInfo.Arguments);
        Assert.Equal("C:\\Work", startInfo.WorkingDirectory);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.True(startInfo.UseShellExecute);
    }

    [Theory]
    [InlineData(42, 42, 84, false)]
    [InlineData(84, 42, 84, false)]
    [InlineData(126, 42, 84, true)]
    [InlineData(0, 42, 84, false)]
    public void ForegroundMonitoringDismissesOnlyForAnUnrelatedWindow(
        int foreground,
        int explorer,
        int owner,
        bool expected)
    {
        Assert.Equal(
            expected,
            ExplorerQuickMenu.ShouldDismissForForeground(
                new IntPtr(foreground),
                new IntPtr(explorer),
                new IntPtr(owner)));
    }
}
