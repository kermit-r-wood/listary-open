using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookHostPathsTests
{
    [Fact]
    public void ForArchitectureUsesProgramDirectoryHooksSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-hook-paths-" + Guid.NewGuid());
        var paths = new HookHostPaths(root);

        var x64 = paths.ForArchitecture(HookArchitecture.X64);
        var x86 = paths.ForArchitecture(HookArchitecture.X86);

        Assert.Equal(Path.Combine(root, "hooks", "x64", "ListaryOpen.HookHost.exe"), x64.HostExePath);
        Assert.Equal(Path.Combine(root, "hooks", "x64", "ListaryOpen.Hook.dll"), x64.HookDllPath);
        Assert.Equal(Path.Combine(root, "hooks", "x86", "ListaryOpen.HookHost.exe"), x86.HostExePath);
        Assert.Equal(Path.Combine(root, "hooks", "x86", "ListaryOpen.Hook.dll"), x86.HookDllPath);
    }

    [Fact]
    public void SnapshotReportsDllPresenceSeparatelyFromHostPresence()
    {
        var root = Directory.CreateTempSubdirectory("listary-open-hook-paths-");
        try
        {
            var x64Folder = Directory.CreateDirectory(Path.Combine(root.FullName, "hooks", "x64"));
            File.WriteAllText(Path.Combine(x64Folder.FullName, "ListaryOpen.Hook.dll"), string.Empty);
            var paths = new HookHostPaths(root.FullName);

            var snapshot = paths.ForArchitecture(HookArchitecture.X64).Snapshot();

            Assert.False(snapshot.HostExists);
            Assert.True(snapshot.HookDllExists);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
