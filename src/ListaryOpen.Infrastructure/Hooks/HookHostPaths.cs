using System.IO;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookHostPaths : IDisposable
{
    private const string SessionDirectoryPrefix = "session-";
    private readonly string _programDirectory;
    private readonly string? _ownedRuntimeDirectory;
    private int _disposed;

    public HookHostPaths(string programDirectory)
        : this(programDirectory, ownedRuntimeDirectory: null)
    {
    }

    private HookHostPaths(string programDirectory, string? ownedRuntimeDirectory)
    {
        if (string.IsNullOrWhiteSpace(programDirectory))
        {
            throw new ArgumentException("Program directory cannot be empty.", nameof(programDirectory));
        }

        _programDirectory = Path.GetFullPath(programDirectory);
        _ownedRuntimeDirectory = ownedRuntimeDirectory is null
            ? null
            : Path.GetFullPath(ownedRuntimeDirectory);
    }

    public static HookHostPaths CreateDefault()
    {
        var programDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        // Inject from the installed/published code directory. Firefox's sandbox
        // rejects hook DLLs copied to a new per-user AppData session directory,
        // even when those bytes are identical to the installed DLL. A stable code
        // path also lets Windows resolve the adjacent native runtime consistently.
        // Authenticated HookHost shutdown unloads the DLL before application exit,
        // so the distributable remains replaceable after a graceful shutdown.
        return new HookHostPaths(programDirectory);
    }

    internal static HookHostPaths CreateRuntimeCopy(string programDirectory, string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        var sourceProgramDirectory = Path.GetFullPath(programDirectory);
        var sourceHooksDirectory = Path.Combine(sourceProgramDirectory, "hooks");
        if (!Directory.Exists(sourceHooksDirectory))
        {
            return new HookHostPaths(sourceProgramDirectory);
        }

        var fullRuntimeRoot = Path.GetFullPath(runtimeRoot);
        Directory.CreateDirectory(fullRuntimeRoot);
        DeleteStaleRuntimeDirectories(fullRuntimeRoot);

        var sessionDirectory = Path.Combine(
            fullRuntimeRoot,
            $"{SessionDirectoryPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionDirectory);
        try
        {
            CopyDirectory(sourceHooksDirectory, Path.Combine(sessionDirectory, "hooks"));
            return new HookHostPaths(sessionDirectory, sessionDirectory);
        }
        catch
        {
            TryDeleteDirectory(sessionDirectory);
            throw;
        }
    }

    public HookArchitecturePaths ForArchitecture(HookArchitecture architecture)
    {
        var folder = Path.Combine(_programDirectory, "hooks", architecture.ToFolderName());
        return new HookArchitecturePaths(
            architecture,
            folder,
            Path.Combine(folder, "ListaryOpen.HookHost.exe"),
            Path.Combine(folder, "ListaryOpen.Hook.dll"));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || _ownedRuntimeDirectory is null)
        {
            return;
        }

        // An injected DLL can take a little longer than its host to leave the
        // target process. If so, retain this disposable copy and retry it at the
        // next app launch; the original package directory is still unlocked.
        TryDeleteDirectory(_ownedRuntimeDirectory);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            var destination = Path.Combine(
                destinationDirectory,
                Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static void DeleteStaleRuntimeDirectories(string runtimeRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     runtimeRoot,
                     SessionDirectoryPrefix + "*",
                     SearchOption.TopDirectoryOnly))
        {
            TryDeleteDirectory(directory);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException)
        {
        }
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
