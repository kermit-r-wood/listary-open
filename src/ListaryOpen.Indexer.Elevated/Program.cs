using System.Text.Json;
using ListaryOpen.Indexer.Elevated;
using ListaryOpen.Indexer.Elevated.Ntfs;

if (args.Length == 2 && args[0] == "scan")
{
    var root = args[1];
    try
    {
        _ = NtfsScanRoot.Create(root);
    }
    catch (ArgumentException exception)
    {
        Console.Error.WriteLine(exception.Message);
        Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
        return 2;
    }

    try
    {
        var reader = new NtfsUsnJournalReader();
        await foreach (var record in reader.EnumerateVolumeAsync(root, CancellationToken.None))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    record.FullPath,
                    record.IsDirectory,
                    record.SizeBytes,
                    record.LastWriteTime
                },
                JsonOptions.Default));
        }

        return 0;
    }
    catch (Exception exception) when (ElevatedHelperExitCodeClassifier.IsNtfsAccessFailure(exception))
    {
        Console.Error.WriteLine(exception.Message);
        return 5;
    }
}

Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
return 2;

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
}
