namespace ListaryOpen.Core.Search;

/// <summary>
/// Heuristic for application-oriented search results (shortcuts and common app executables).
/// </summary>
public static class ApplicationPath
{
    private static readonly string[] AppRootMarkers =
    [
        "\\start menu\\",
        "\\programs\\",
        "\\program files\\",
        "\\program files (x86)\\",
        "\\windowsapps\\",
        "\\application data\\microsoft\\windows\\start menu\\"
    ];

    public static bool IsApplication(string? fullPath, bool isDirectory)
    {
        if (isDirectory || string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".msc", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = fullPath.Replace('/', '\\').ToLowerInvariant();
        foreach (var marker in AppRootMarkers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        // Bare .exe under user Desktop is often an app shortcut target placement.
        if (normalized.Contains("\\desktop\\", StringComparison.Ordinal)
            && extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
