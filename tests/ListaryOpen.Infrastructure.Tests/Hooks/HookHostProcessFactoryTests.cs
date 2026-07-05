using System.Diagnostics;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookHostProcessFactoryTests
{
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
}
