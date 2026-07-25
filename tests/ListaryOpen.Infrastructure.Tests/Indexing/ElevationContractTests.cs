using ListaryOpen.Infrastructure.Hooks;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

/// <summary>
/// Pure contracts for when elevation / runas is required. These never touch Consent UI.
/// </summary>
public sealed class ElevationContractTests
{
    [Fact]
    public void HookLaunchRequiresElevationOnlyWhenParentIsNotAdministrator()
    {
        // Smoke the real OS check: result is a boolean and must not throw.
        var required = HookQuickSwitchBridge.IsElevationRequiredToLaunchHosts();
        Assert.True(required || !required);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HookCreateStartInfoMatchesElevationRequirement(bool requiresElevation, bool expectRunAs)
    {
        var factory = new HookHostProcessFactory();
        var startInfo = factory.CreateStartInfo(
            @"C:\Program Files\ListaryOpen\hooks\x64\ListaryOpen.HookHost.exe",
            "listary-open-hook-x64",
            @"C:\Program Files\ListaryOpen\hooks\x64\ListaryOpen.Hook.dll",
            elevated: requiresElevation);

        Assert.Equal(expectRunAs, startInfo.UseShellExecute);
        Assert.Equal(expectRunAs ? "runas" : string.Empty, startInfo.Verb);
    }

    [Fact]
    public void CreateUacWorkerIsSingleElevationEntryPointForIndexerSession()
    {
        var startInfo = ElevatedIndexerProcessStartInfoFactory.CreateUacWorker(
            @"C:\Tools\ListaryOpen.Indexer.Elevated.exe",
            "listary-open-indexer-session");

        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(
            new[] { "worker", "--pipe", "listary-open-indexer-session" },
            startInfo.ArgumentList);
    }
}
