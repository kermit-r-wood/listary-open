using System.ComponentModel;
using System.Diagnostics;

namespace ListaryOpen.IntegrationTests;

internal static class NativeModuleTestSupport
{
    public static string? FindModulePathByFileName(int processId, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.Modules.Cast<ProcessModule>()
                .Select(module => module.FileName)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(path),
                    fileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or InvalidOperationException
                                           or Win32Exception)
        {
            return null;
        }
    }

    public static bool AreModulePathsUnloaded(int processId, IEnumerable<string> expectedPaths)
    {
        ArgumentNullException.ThrowIfNull(expectedPaths);
        var normalizedExpectedPaths = expectedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var process = Process.GetProcessById(processId);
            var loadedPaths = process.Modules.Cast<ProcessModule>()
                .Select(module => Path.GetFullPath(module.FileName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return !loadedPaths.Overlaps(normalizedExpectedPaths);
        }
        catch (ArgumentException)
        {
            // A terminated target cannot retain an injected module.
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Failure to enumerate a live process is not proof of unload.
            return false;
        }
    }
}
