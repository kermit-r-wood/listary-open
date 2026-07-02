using System.Text.Json;

if (args.Length == 2 && args[0] == "scan")
{
    var root = args[1];
    Console.WriteLine(JsonSerializer.Serialize(new { type = "scan-started", root }));
    return 0;
}

Console.Error.WriteLine("Usage: ListaryOpen.Indexer.Elevated scan <root>");
return 2;
