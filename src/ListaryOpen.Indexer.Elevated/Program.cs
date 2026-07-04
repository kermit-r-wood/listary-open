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

Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan-to-file <root> <records-path> <error-path>");
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
        var errorDirectory = Path.GetDirectoryName(errorPath);
        if (!string.IsNullOrWhiteSpace(errorDirectory))
        {
            Directory.CreateDirectory(errorDirectory);
        }

        File.WriteAllText(errorPath, message);
    }
    catch
    {
    }
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
