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

    [Fact]
    public void RuntimeCopyStagesEveryHookFileOutsideProgramDirectoryAndCleansItUp()
    {
        var programRoot = Directory.CreateTempSubdirectory("listary-open-hook-source-");
        var runtimeRoot = Directory.CreateTempSubdirectory("listary-open-hook-runtime-");
        try
        {
            var staleDirectory = Directory.CreateDirectory(
                Path.Combine(runtimeRoot.FullName, "session-stale"));
            File.WriteAllText(Path.Combine(staleDirectory.FullName, "old.dll"), "old");
            foreach (var architecture in new[] { "x64", "x86" })
            {
                var architectureDirectory = Directory.CreateDirectory(
                    Path.Combine(programRoot.FullName, "hooks", architecture));
                File.WriteAllText(Path.Combine(architectureDirectory.FullName, "ListaryOpen.HookHost.exe"), "host");
                File.WriteAllText(Path.Combine(architectureDirectory.FullName, "ListaryOpen.Hook.dll"), "hook");
                File.WriteAllText(Path.Combine(architectureDirectory.FullName, "libunwind.dll"), "runtime");
            }

            string stagedDirectory;
            using (var paths = HookHostPaths.CreateRuntimeCopy(programRoot.FullName, runtimeRoot.FullName))
            {
                var x64 = paths.ForArchitecture(HookArchitecture.X64);
                var x86 = paths.ForArchitecture(HookArchitecture.X86);
                stagedDirectory = Directory.GetParent(Directory.GetParent(x64.DirectoryPath)!.FullName)!.FullName;

                Assert.False(Path.GetFullPath(x64.HostExePath).StartsWith(
                    Path.GetFullPath(programRoot.FullName) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                Assert.Equal("host", File.ReadAllText(x64.HostExePath));
                Assert.Equal("hook", File.ReadAllText(x64.HookDllPath));
                Assert.Equal(
                    "runtime",
                    File.ReadAllText(Path.Combine(x64.DirectoryPath, "libunwind.dll")));
                Assert.Equal("host", File.ReadAllText(x86.HostExePath));
                Assert.False(Directory.Exists(staleDirectory.FullName));
            }

            Assert.False(Directory.Exists(stagedDirectory));
        }
        finally
        {
            programRoot.Delete(recursive: true);
            runtimeRoot.Delete(recursive: true);
        }
    }
}
