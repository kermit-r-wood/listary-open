using ListaryOpen.Indexer.Elevated.Ntfs;

namespace ListaryOpen.Indexer.Elevated;

/// <summary>
/// Shared NTFS helper commands used by both one-shot CLI invocations and the
/// long-lived elevated worker (single UAC session).
/// </summary>
internal static class ElevatedIndexerCommands
{
    public static async Task<int> ScanToFileAsync(string root, string recordsPath, string errorPath)
    {
        if (!ElevatedIndexerOutputPathValidator.AreAllowed(recordsPath, errorPath)
            && !ElevatedIndexerOutputPathValidator.AreAllowedBinaryRecords(recordsPath, errorPath))
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
            if (string.Equals(Path.GetExtension(recordsPath), ".bin", StringComparison.OrdinalIgnoreCase))
            {
                await ElevatedIndexerRecordWriter.WriteBinaryFileAsync(
                        reader.EnumerateVolumeAsync(root, CancellationToken.None),
                        recordsPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await ElevatedIndexerRecordWriter.WriteFileAsync(
                        reader.EnumerateVolumeAsync(root, CancellationToken.None),
                        recordsPath,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

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

    public static async Task<int> JournalStateToFileAsync(string root, string statePath, string errorPath)
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

    public static async Task<int> ReadJournalToFileAsync(
        string root,
        string expectedUsnJournalIdText,
        string startUsnText,
        string endUsnText,
        string changesPath,
        string errorPath)
    {
        if (!ulong.TryParse(
                expectedUsnJournalIdText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var expectedUsnJournalId)
            || !long.TryParse(
                startUsnText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var startUsn)
            || !long.TryParse(
                endUsnText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var endUsn)
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
            WriteErrorFile(
                errorPath,
                "Usage: ListaryOpen.Indexer.Elevated read-journal-to-file <root> <expected-journal-id> <start-usn> <end-usn> <changes-path> <error-path>");
            return 2;
        }

        try
        {
            var reader = new NtfsUsnJournalReader();
            await ElevatedIndexerRecordWriter.WriteJournalChangesFileAsync(
                    reader.EnumerateChangesAsync(
                        root,
                        expectedUsnJournalId,
                        startUsn,
                        endUsn,
                        CancellationToken.None),
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

    public static bool TryValidateRoot(string root, Action<string> writeError)
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

    public static void WriteErrorFile(string errorPath, string message)
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
}
