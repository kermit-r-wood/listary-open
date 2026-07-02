using System.ComponentModel;

namespace ListaryOpen.Indexer.Elevated;

internal static class ElevatedHelperExitCodeClassifier
{
    public static bool IsNtfsAccessFailure(Exception exception)
    {
        return exception is Win32Exception or UnauthorizedAccessException;
    }
}
