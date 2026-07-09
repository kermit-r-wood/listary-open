namespace ListaryOpen.Infrastructure.Tests.Tools;

public sealed class HookQuickSwitchDialogProbeScriptTests
{
    [Fact]
    public void ProbeScriptExposesProcessSelectionForegroundingAndHookIpc()
    {
        var script = ReadScript();

        Assert.Contains("[string[]]$ProcessNames", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$NoJump", script, StringComparison.Ordinal);
        Assert.Contains("PowerShell 7+", script, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow", script, StringComparison.Ordinal);
        Assert.Contains("#32770", script, StringComparison.Ordinal);
        Assert.Contains("CustomFileBrowser", script, StringComparison.Ordinal);
        Assert.Contains("GHOST_WindowClass", script, StringComparison.Ordinal);
        Assert.Contains("Test-PreferredDialogTitle", script, StringComparison.Ordinal);
        Assert.Contains("GetActiveDialogResultAsync", script, StringComparison.Ordinal);
        Assert.Contains("JumpDialogToFolderAsync", script, StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        return File.ReadAllText(Path.Combine(FindRepositoryRoot(), "tools", "test-hook-quick-switch-dialog.ps1"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ListaryOpen.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
