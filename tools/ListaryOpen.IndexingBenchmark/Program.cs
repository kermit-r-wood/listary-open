using ListaryOpen.IndexingBenchmark;

try
{
    if (args.Length > 0 && string.Equals(args[0], "--probe-reconciliation", StringComparison.Ordinal))
    {
        return await ExistingDatabaseProbe.RunAsync(args.Skip(1).ToArray(), CancellationToken.None);
    }

    if (args.Length > 0 && string.Equals(args[0], "--analyze-scan-output", StringComparison.Ordinal))
    {
        return await ScanOutputAnalyzer.RunAsync(args.Skip(1).ToArray(), CancellationToken.None);
    }

    if (args.Length > 0 && string.Equals(args[0], "--probe-short-alias", StringComparison.Ordinal))
    {
        return await ShortAliasProbe.RunAsync(args.Skip(1).ToArray(), CancellationToken.None);
    }

    var options = BenchmarkOptions.Parse(args);
    var report = await IndexingBenchmarkRunner.RunAsync(options, CancellationToken.None);
    BenchmarkReportWriter.WriteConsole(report);
    if (!string.IsNullOrWhiteSpace(options.JsonOutputPath))
    {
        await BenchmarkReportWriter.WriteJsonAsync(report, options.JsonOutputPath, CancellationToken.None);
    }

    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
