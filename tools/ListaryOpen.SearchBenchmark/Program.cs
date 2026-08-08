using ListaryOpen.SearchBenchmark;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

try
{
    if (string.Equals(args[0], "ab", StringComparison.OrdinalIgnoreCase))
    {
        return await AblationBenchmarkRunner.RunAsync(args[1..], CancellationToken.None);
    }

    if (string.Equals(args[0], "nametable-ab", StringComparison.OrdinalIgnoreCase)
        || string.Equals(args[0], "g11", StringComparison.OrdinalIgnoreCase))
    {
        return await NameTableAblationRunner.RunAsync(args[1..], CancellationToken.None);
    }

    if (string.Equals(args[0], "nametable-v1-memory", StringComparison.OrdinalIgnoreCase))
    {
        return await NameTableLegacySnapshotBenchmarkRunner.RunAsync(args[1..], CancellationToken.None);
    }

    if (string.Equals(args[0], "nametable-v2-load", StringComparison.OrdinalIgnoreCase))
    {
        return await NameTableSnapshotLoadBenchmarkRunner.RunAsync(args[1..], CancellationToken.None);
    }

    if (string.Equals(args[0], "search", StringComparison.OrdinalIgnoreCase))
    {
        return await SearchBenchmarkRunner.RunAsync(args[1..], CancellationToken.None);
    }

    // Preserve the original CLI: <index.db> [--root <path>] <query> [...].
    return await SearchBenchmarkRunner.RunAsync(args, CancellationToken.None);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  ListaryOpen.SearchBenchmark search <index.db> [--root <path>] <query> [query ...]");
    Console.Error.WriteLine("  ListaryOpen.SearchBenchmark ab <index.db> --root <path> --query <query> [--iterations N] [--sample N] [--upserts N]");
    Console.Error.WriteLine("  ListaryOpen.SearchBenchmark nametable-ab [--backend nametable|sqlite|both] [--records N] [--warmup N] [--iterations N] [--json path]");
    Console.Error.WriteLine("  ListaryOpen.SearchBenchmark nametable-v1-memory --snapshot <index.losn.tmp> [--json report.json] [--write-v2 snapshot.losn]");
    Console.Error.WriteLine("  ListaryOpen.SearchBenchmark nametable-v2-load --snapshot <index.losn> [--json report.json]");
}
