using System.IO;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookHostPaths
{
    private readonly string _programDirectory;

    public HookHostPaths(string programDirectory)
    {
        if (string.IsNullOrWhiteSpace(programDirectory))
        {
            throw new ArgumentException("Program directory cannot be empty.", nameof(programDirectory));
        }

        _programDirectory = Path.GetFullPath(programDirectory);
    }

    public static HookHostPaths CreateDefault() => new(AppContext.BaseDirectory);

    public HookArchitecturePaths ForArchitecture(HookArchitecture architecture)
    {
        var folder = Path.Combine(_programDirectory, "hooks", architecture.ToFolderName());
        return new HookArchitecturePaths(
            architecture,
            folder,
            Path.Combine(folder, "ListaryOpen.HookHost.exe"),
            Path.Combine(folder, "ListaryOpen.Hook.dll"));
    }
}

public sealed record HookArchitecturePaths(
    HookArchitecture Architecture,
    string DirectoryPath,
    string HostExePath,
    string HookDllPath)
{
    public HookArchitecturePathSnapshot Snapshot() => new(
        Architecture,
        File.Exists(HostExePath),
        File.Exists(HookDllPath),
        HostExePath,
        HookDllPath);
}

public sealed record HookArchitecturePathSnapshot(
    HookArchitecture Architecture,
    bool HostExists,
    bool HookDllExists,
    string HostExePath,
    string HookDllPath);
