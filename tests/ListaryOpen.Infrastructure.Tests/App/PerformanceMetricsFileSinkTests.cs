using System.Text.Json;
using ListaryOpen.App;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class PerformanceMetricsFileSinkTests
{
    [Fact]
    public void DisposeFlushesQueuedMetricsAsJsonLines()
    {
        var directory = Directory.CreateTempSubdirectory("listary-open-metrics-");
        var path = Path.Combine(directory.FullName, "metrics.jsonl");
        try
        {
            using (var sink = new PerformanceMetricsFileSink(path))
            using (var measurement = PerformanceMetrics.Begin("test.async_sink"))
            {
                measurement.Complete("success", resultCount: 3);
            }

            var matchingLine = File.ReadAllLines(path)
                .Single(line => line.Contains("test.async_sink", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(matchingLine);
            Assert.Equal("test.async_sink", document.RootElement.GetProperty("Operation").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("ResultCount").GetInt32());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void OversizedMetricsFileIsRotatedBeforeWritingNewBatch()
    {
        var directory = Directory.CreateTempSubdirectory("listary-open-metrics-rotate-");
        var path = Path.Combine(directory.FullName, "metrics.jsonl");
        try
        {
            File.WriteAllText(path, new string('x', 128));
            using (var sink = new PerformanceMetricsFileSink(
                       path,
                       maximumFileSize: 64,
                       retainedFileCount: 2,
                       batchDelay: TimeSpan.Zero))
            using (var measurement = PerformanceMetrics.Begin("test.rotated_sink"))
            {
                measurement.Complete("success");
            }

            Assert.True(File.Exists(path + ".1"));
            Assert.Equal(128, new FileInfo(path + ".1").Length);
            Assert.Contains("test.rotated_sink", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
