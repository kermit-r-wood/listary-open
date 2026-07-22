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
}
