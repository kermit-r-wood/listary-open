using System.Diagnostics;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookHostProcessFactoryTests
{
    [Fact]
    public void TerminateDoesNotReturnUntilTheOwnedProcessHasExited()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        var processId = process.Id;

        new HookHostProcessFactory().Terminate(process);

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [Fact]
    public void GracefulTerminationWaitsForNaturalExit()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"listary-open-hook-graceful-{Guid.NewGuid():N}.txt");
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add($"Start-Sleep -Milliseconds 200; Set-Content -LiteralPath '{marker}' -Value complete");
        var process = Process.Start(startInfo);
        Assert.NotNull(process);

        try
        {
            new HookHostProcessFactory().WaitForGracefulExitOrTerminate(process);

            Assert.True(File.Exists(marker));
            Assert.Equal("complete", File.ReadAllText(marker).Trim());
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public void GracefulTerminationTimeoutDetachesWithoutKillingTheHost()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command Start-Sleep -Seconds 30",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        var processId = process.Id;

        new HookHostProcessFactory().WaitForGracefulExitOrTerminate(process);

        using var stillRunning = Process.GetProcessById(processId);
        try
        {
            Assert.False(stillRunning.HasExited);
        }
        finally
        {
            if (!stillRunning.HasExited)
            {
                stillRunning.Kill(entireProcessTree: true);
                stillRunning.WaitForExit();
            }
        }
    }

    [Fact]
    public void CreateStartInfoForNonElevatedHostUsesNoConsoleAndPipeDllArguments()
    {
        var factory = new HookHostProcessFactory();

        var startInfo = factory.CreateStartInfo(
            "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.HookHost.exe",
            "listary-open-hook-x64",
            "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll",
            elevated: false);

        Assert.Equal("C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.HookHost.exe", startInfo.FileName);
        Assert.Equal("C:\\Program Files\\ListaryOpen\\hooks\\x64", startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Verb);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(
            new[]
            {
                "--pipe",
                "listary-open-hook-x64",
                "--dll",
                "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll"
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfoForElevatedHostUsesRunAsAndPipeDllArguments()
    {
        var factory = new HookHostProcessFactory();

        var startInfo = factory.CreateStartInfo(
            "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.HookHost.exe",
            "listary-open-hook-x86",
            "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.Hook.dll",
            elevated: true);

        Assert.Equal("C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.HookHost.exe", startInfo.FileName);
        Assert.Equal("C:\\Program Files\\ListaryOpen\\hooks\\x86", startInfo.WorkingDirectory);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle);
        Assert.Equal(
            new[]
            {
                "--pipe",
                "listary-open-hook-x86",
                "--dll",
                "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.Hook.dll"
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfoIncludesParentAndSecretForBoundHost()
    {
        var factory = new HookHostProcessFactory();

        var startInfo = factory.CreateStartInfo(
            "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.HookHost.exe",
            "listary-open-hook-x64-123-session",
            "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll",
            elevated: true,
            parentProcessId: 123,
            secret: "session-secret");

        Assert.Contains("--parent-pid", startInfo.ArgumentList);
        Assert.Contains("123", startInfo.ArgumentList);
        Assert.Contains("--secret", startInfo.ArgumentList);
        Assert.Contains("session-secret", startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfoCanAttachChildHostLaunchArguments()
    {
        var factory = new HookHostProcessFactory();

        var startInfo = factory.CreateStartInfo(
            "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.HookHost.exe",
            "listary-open-hook-x64",
            "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll",
            elevated: true,
            new[]
            {
                new HookHostLaunchRequest(
                    "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.HookHost.exe",
                    "listary-open-hook-x86",
                    "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.Hook.dll")
            });

        Assert.Equal(
            new[]
            {
                "--pipe",
                "listary-open-hook-x64",
                "--dll",
                "C:\\Program Files\\ListaryOpen\\hooks\\x64\\ListaryOpen.Hook.dll",
                "--launch-host",
                "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.HookHost.exe",
                "--launch-pipe",
                "listary-open-hook-x86",
                "--launch-dll",
                "C:\\Program Files\\ListaryOpen\\hooks\\x86\\ListaryOpen.Hook.dll"
            },
            startInfo.ArgumentList);
    }
}
