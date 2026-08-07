using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ListaryOpen.Indexer.Elevated;
using ListaryOpen.Indexer.Elevated.Ntfs;

// Prefer not to contend with interactive UI while scanning MFT / USN.
TryLowerProcessPriority();

if (args.Length == 3 && args[0] == "worker" && args[1] == "--pipe")
{
    return await ElevatedIndexerPipeWorker.RunAsync(args[2]).ConfigureAwait(false);
}

if (args.Length == 2 && args[0] == "scan")
{
    return await ScanToConsoleAsync(args[1]).ConfigureAwait(false);
}

if (args.Length == 4 && args[0] == "scan-to-file")
{
    return await ElevatedIndexerCommands.ScanToFileAsync(args[1], args[2], args[3]).ConfigureAwait(false);
}

if (args.Length == 4 && args[0] == "journal-state-to-file")
{
    return await ElevatedIndexerCommands.JournalStateToFileAsync(args[1], args[2], args[3]).ConfigureAwait(false);
}

if (args.Length == 7 && args[0] == "read-journal-to-file")
{
    return await ElevatedIndexerCommands.ReadJournalToFileAsync(
            args[1],
            args[2],
            args[3],
            args[4],
            args[5],
            args[6])
        .ConfigureAwait(false);
}

Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated worker --pipe <pipe-name>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan-to-file <root> <records-path> <error-path>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated journal-state-to-file <root> <state-path> <error-path>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated read-journal-to-file <root> <expected-journal-id> <start-usn> <end-usn> <changes-path> <error-path>");
return 2;

static void TryLowerProcessPriority()
{
    try
    {
        using var process = Process.GetCurrentProcess();
        // PROCESS_MODE_BACKGROUND_BEGIN lowers CPU, disk-I/O, and memory
        // priority. BelowNormal alone leaves raw-volume reads at normal I/O
        // priority and can still make the machine feel saturated.
        if (!IndexerPriorityNativeMethods.SetPriorityClass(process.Handle, 0x00100000))
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
    }
    catch (Exception exception) when (exception is InvalidOperationException
                                          or PlatformNotSupportedException
                                          or System.ComponentModel.Win32Exception)
    {
        // Best-effort only; indexing still works at the default priority.
    }
}

static async Task<int> ScanToConsoleAsync(string root)
{
    if (!ElevatedIndexerCommands.TryValidateRoot(root, Console.Error.WriteLine))
    {
        Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
        return 2;
    }

    try
    {
        var reader = new NtfsUsnJournalReader();
        await ElevatedIndexerRecordWriter.WriteAsync(
                reader.EnumerateVolumeAsync(root, CancellationToken.None),
                Console.Out,
                CancellationToken.None)
            .ConfigureAwait(false);

        return 0;
    }
    catch (Exception exception) when (ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(exception))
    {
        Console.Error.WriteLine(exception.Message);
        return 5;
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}

internal static class IndexerPriorityNativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetPriorityClass(IntPtr processHandle, uint priorityClass);
}
