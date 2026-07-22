using ListaryOpen.IndexingBenchmark;

try
{
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
