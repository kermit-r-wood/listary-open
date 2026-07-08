using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Tests.Tools;

public sealed class VerifyHookModulesScriptTests
{
    [Fact]
    public async Task DefaultModeReportsMissingRequiredTargetsForUnknownProcessName()
    {
        var missingProcessName = "__listary_missing_target_" + Guid.NewGuid().ToString("N");
        using var process = StartPowerShell(
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            Path.Combine(FindRepositoryRoot(), "tools", "verify-hook-modules.ps1"),
            "-ProcessNames",
            missingProcessName,
            "-SkipDialogPing");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var processTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(processTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Hook verification script did not exit within the test timeout.");
        }

        using var outputTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(outputTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Hook verification script output pipes did not close within the test timeout.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("RequiredTargetMissing", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("Required target dialog owner processes were not found.", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptExposesSubsetAndExpectedHookRootControls()
    {
        var script = ReadScript();

        Assert.Contains("[switch]$AllowSubset", script, StringComparison.Ordinal);
        Assert.Contains("[string]$ExpectedHooksRoot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptReportsMissingRequiredDialogOwnerTargetsDistinctly()
    {
        var script = ReadScript();

        Assert.Contains("RequiredTargetMissing", script, StringComparison.Ordinal);
        Assert.Contains("Required target dialog owner processes were not found.", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptRejectsHookModuleNameMatchesOutsideExpectedHookRoot()
    {
        var script = ReadScript();

        Assert.Contains("HookPathMismatch", script, StringComparison.Ordinal);
        Assert.Contains("Test-PathUnderRoot", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AllMatchingProcessesUsesFlexibleTargetNameMatching()
    {
        var script = ReadScript();

        Assert.DoesNotContain("Get-Process -Name $targetName", script, StringComparison.Ordinal);
        Assert.Contains("Test-TargetProcessName -ProcessName $process.ProcessName -TargetNames $TargetNames", script, StringComparison.Ordinal);
    }

    [Fact]
    public void HookModuleVerificationRefreshesProcessByIdAfterDialogPing()
    {
        var script = ReadScript();

        Assert.Contains("Get-Process -Id $processId -ErrorAction Stop", script, StringComparison.Ordinal);
    }

    [Fact]
    public void HookModuleVerificationRejectsPidReuseBeforeModuleEnumeration()
    {
        var script = ReadScript();

        Assert.Contains("ProcessIdentityChanged", script, StringComparison.Ordinal);
        Assert.Contains("Test-SameProcessInstance", script, StringComparison.Ordinal);
        Assert.Contains("[object]$OriginalStartTime", script, StringComparison.Ordinal);
    }

    private static string ReadScript()
    {
        return File.ReadAllText(Path.Combine(FindRepositoryRoot(), "tools", "verify-hook-modules.ps1"));
    }

    private static Process StartPowerShell(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process;
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
