using System.Text.Json;
using ListaryOpen.Indexer.Elevated;
using ListaryOpen.Indexer.Elevated.Ntfs;

if (args.Length == 2 && args[0] == "scan")
{
    return await ScanToConsoleAsync(args[1]).ConfigureAwait(false);
}

if (args.Length == 4 && args[0] == "scan-to-file")
{
    return await ScanToFileAsync(args[1], args[2], args[3]).ConfigureAwait(false);
}

if (args.Length == 4 && args[0] == "journal-state-to-file")
{
    return await JournalStateToFileAsync(args[1], args[2], args[3]).ConfigureAwait(false);
}

if (args.Length == 7 && args[0] == "read-journal-to-file")
{
    return await ReadJournalToFileAsync(args[1], args[2], args[3], args[4], args[5], args[6]).ConfigureAwait(false);
}

Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan-to-file <root> <records-path> <error-path>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated journal-state-to-file <root> <state-path> <error-path>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated read-journal-to-file <root> <expected-journal-id> <start-usn> <end-usn> <changes-path> <error-path>");
return 2;

static async Task<int> ScanToConsoleAsync(string root)
{
    if (!TryValidateRoot(root, Console.Error.WriteLine))
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

static async Task<int> ScanToFileAsync(string root, string recordsPath, string errorPath)
{
    if (!ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath))
    {
        return 2;
    }

    if (!TryValidateRoot(root, message => WriteErrorFile(errorPath, message)))
    {
        WriteErrorFile(errorPath, "Usage: ListaryOpen.Indexer.Elevated scan-to-file <root> <records-path> <error-path>");
        return 2;
    }

    try
    {
        var reader = new NtfsUsnJournalReader();
        await ElevatedIndexerRecordWriter.WriteFileAsync(
                reader.EnumerateVolumeAsync(root, CancellationToken.None),
                recordsPath,
                CancellationToken.None)
            .ConfigureAwait(false);

        return 0;
    }
    catch (Exception exception) when (ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(exception))
    {
        WriteErrorFile(errorPath, exception.Message);
        return 5;
    }
    catch (Exception exception)
    {
        WriteErrorFile(errorPath, exception.Message);
        return 1;
    }
}

static async Task<int> JournalStateToFileAsync(string root, string statePath, string errorPath)
{
    if (!ElevatedIndexerOutputPathValidator.AreAllowed(statePath, errorPath))
    {
        return 2;
    }

    if (!TryValidateRoot(root, message => WriteErrorFile(errorPath, message)))
    {
        WriteErrorFile(errorPath, "Usage: ListaryOpen.Indexer.Elevated journal-state-to-file <root> <state-path> <error-path>");
        return 2;
    }

    try
    {
        var reader = new NtfsUsnJournalReader();
        await ElevatedIndexerRecordWriter.WriteJournalStateFileAsync(
                reader.ReadJournalState(root),
                statePath,
                CancellationToken.None)
            .ConfigureAwait(false);

        return 0;
    }
    catch (Exception exception) when (ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(exception))
    {
        WriteErrorFile(errorPath, exception.Message);
        return 5;
    }
    catch (Exception exception)
    {
        WriteErrorFile(errorPath, exception.Message);
        return 1;
    }
}

static async Task<int> ReadJournalToFileAsync(
    string root,
    string expectedUsnJournalIdText,
    string startUsnText,
    string endUsnText,
    string changesPath,
    string errorPath)
{
    if (!ulong.TryParse(expectedUsnJournalIdText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var expectedUsnJournalId)
        || !long.TryParse(startUsnText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var startUsn)
        || !long.TryParse(endUsnText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var endUsn)
        || startUsn > endUsn)
    {
        WriteErrorFile(errorPath, "USN range is invalid.");
        return 2;
    }

    if (!ElevatedIndexerOutputPathValidator.AreAllowed(changesPath, errorPath))
    {
        return 2;
    }

    if (!TryValidateRoot(root, message => WriteErrorFile(errorPath, message)))
    {
        WriteErrorFile(errorPath, "Usage: ListaryOpen.Indexer.Elevated read-journal-to-file <root> <expected-journal-id> <start-usn> <end-usn> <changes-path> <error-path>");
        return 2;
    }

    try
    {
        var reader = new NtfsUsnJournalReader();
        await ElevatedIndexerRecordWriter.WriteJournalChangesFileAsync(
                reader.EnumerateChangesAsync(root, expectedUsnJournalId, startUsn, endUsn, CancellationToken.None),
                changesPath,
                CancellationToken.None)
            .ConfigureAwait(false);

        return 0;
    }
    catch (Exception exception) when (ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(exception))
    {
        WriteErrorFile(errorPath, exception.Message);
        return 5;
    }
    catch (Exception exception)
    {
        WriteErrorFile(errorPath, exception.Message);
        return 1;
    }
}

static bool TryValidateRoot(string root, Action<string> writeError)
{
    try
    {
        _ = NtfsScanRoot.Create(root);
        return true;
    }
    catch (ArgumentException exception)
    {
        writeError(exception.Message);
        return false;
    }
}

static void WriteErrorFile(string errorPath, string message)
{
    if (!ElevatedIndexerOutputPathValidator.IsAllowedErrorPath(errorPath))
    {
        return;
    }

    try
    {
        using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(errorPath, ".err");
        using var writer = new StreamWriter(stream);
        writer.Write(message);
    }
    catch
    {
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
