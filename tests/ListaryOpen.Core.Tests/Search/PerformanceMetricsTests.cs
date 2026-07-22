using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class PerformanceMetricsTests
{
    [Fact]
    public void MeasurementCapturesStagesCountersAndOutcome()
    {
        using var measurement = PerformanceMetrics.Begin("test.operation");
        using (PerformanceMetrics.MeasureStage("first"))
        {
            Thread.SpinWait(100);
        }

        PerformanceMetrics.SetCounter("items", 3);
        var snapshot = measurement.Complete("success", 2);

        Assert.Equal("test.operation", snapshot.Operation);
        Assert.Equal("success", snapshot.Outcome);
        Assert.Equal(2, snapshot.ResultCount);
        Assert.Equal(3, snapshot.Counters["items"]);
        Assert.True(snapshot.Stages["first"] >= 0);
        Assert.True(snapshot.TotalMilliseconds >= snapshot.Stages["first"]);
        Assert.Contains("\"Operation\":\"test.operation\"", snapshot.ToJson());
    }
}
