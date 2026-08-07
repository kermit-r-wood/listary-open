namespace ListaryOpen.IndexingBenchmark;

internal sealed record BenchmarkOptions(
    int Records,
    int PipelineRecords,
    int SqliteRecords,
    int ProgressBatches,
    int Repeats,
    int Warmups,
    int ProducerDelayMilliseconds,
    string? JsonOutputPath)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        var records = 20_000;
        var pipelineRecords = 4_096;
        var sqliteRecords = 5_000;
        var progressBatches = 100_000;
        var repeats = 5;
        var warmups = 1;
        var producerDelayMilliseconds = 5;
        string? jsonOutputPath = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--records":
                    records = ReadPositiveInt(args, ref index, argument);
                    break;
                case "--pipeline-records":
                    pipelineRecords = ReadPositiveInt(args, ref index, argument);
                    break;
                case "--sqlite-records":
                    sqliteRecords = ReadPositiveInt(args, ref index, argument);
                    break;
                case "--progress-batches":
                    progressBatches = ReadPositiveInt(args, ref index, argument);
                    break;
                case "--repeats":
                    repeats = ReadPositiveInt(args, ref index, argument);
                    break;
                case "--warmups":
                    warmups = ReadNonNegativeInt(args, ref index, argument);
                    break;
                case "--producer-delay-ms":
                    producerDelayMilliseconds = ReadNonNegativeInt(args, ref index, argument);
                    break;
                case "--json":
                    jsonOutputPath = ReadValue(args, ref index, argument);
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        return new BenchmarkOptions(
            records,
            pipelineRecords,
            sqliteRecords,
            progressBatches,
            repeats,
            warmups,
            producerDelayMilliseconds,
            jsonOutputPath);
    }

    private static int ReadPositiveInt(string[] args, ref int index, string argument)
    {
        var value = ReadNonNegativeInt(args, ref index, argument);
        return value > 0 ? value : throw new ArgumentOutOfRangeException(argument, "Value must be positive.");
    }

    private static int ReadNonNegativeInt(string[] args, ref int index, string argument)
    {
        var value = ReadValue(args, ref index, argument);
        return int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException($"{argument} requires a non-negative integer.");
    }

    private static string ReadValue(string[] args, ref int index, string argument)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{argument} requires a value.");
        }

        return args[index];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -c Release --project tools/ListaryOpen.IndexingBenchmark -- [options]");
        Console.WriteLine("  --records N             Async-enumerator and writer records (default 20000)");
        Console.WriteLine("  --pipeline-records N    Paced helper-pipeline records (default 4096)");
        Console.WriteLine("  --sqlite-records N      SQLite upserts/rescan rows per variant (default 5000)");
        Console.WriteLine("  --progress-batches N    Logical batches for throttle ablation (default 100000)");
        Console.WriteLine("  --repeats N             Measured repetitions (default 5)");
        Console.WriteLine("  --warmups N             Unmeasured repetitions (default 1)");
        Console.WriteLine("  --producer-delay-ms N   Delay per 256 pipeline records (default 5)");
        Console.WriteLine("  --json PATH             Write all raw measurements and summaries as JSON");
    }
}
