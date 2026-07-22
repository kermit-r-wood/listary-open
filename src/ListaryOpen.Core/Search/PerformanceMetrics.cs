using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

namespace ListaryOpen.Core.Search;

public sealed record PerformanceMetricSnapshot(
    Guid Id,
    string Operation,
    DateTimeOffset StartedAt,
    double TotalMilliseconds,
    string Outcome,
    int ResultCount,
    IReadOnlyDictionary<string, double> Stages,
    IReadOnlyDictionary<string, long> Counters)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}

public static class PerformanceMetrics
{
    private static readonly Meter Meter = new("ListaryOpen", "1.0");
    private static readonly Histogram<double> TotalDuration =
        Meter.CreateHistogram<double>("listary.operation.duration", "ms");
    private static readonly Histogram<double> StageDuration =
        Meter.CreateHistogram<double>("listary.operation.stage.duration", "ms");
    private static readonly Counter<long> OperationCount =
        Meter.CreateCounter<long>("listary.operation.count");
    private static readonly AsyncLocal<PerformanceMeasurement?> CurrentMeasurement = new();

    public static event EventHandler<PerformanceMetricSnapshot>? Completed;

    public static PerformanceMeasurement Begin(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var measurement = new PerformanceMeasurement(operation, CurrentMeasurement.Value, Publish);
        CurrentMeasurement.Value = measurement;
        return measurement;
    }

    public static IDisposable MeasureStage(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        return CurrentMeasurement.Value?.MeasureStage(stage) ?? EmptyStage.Instance;
    }

    public static void SetCounter(string name, long value)
    {
        CurrentMeasurement.Value?.SetCounter(name, value);
    }

    private static void Publish(PerformanceMeasurement measurement, PerformanceMetricSnapshot snapshot)
    {
        if (ReferenceEquals(CurrentMeasurement.Value, measurement))
        {
            CurrentMeasurement.Value = measurement.Parent;
        }

        var operationTag = new KeyValuePair<string, object?>("operation", snapshot.Operation);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", snapshot.Outcome);
        TotalDuration.Record(snapshot.TotalMilliseconds, operationTag, outcomeTag);
        OperationCount.Add(1, operationTag, outcomeTag);
        foreach (var stage in snapshot.Stages)
        {
            StageDuration.Record(
                stage.Value,
                operationTag,
                new KeyValuePair<string, object?>("stage", stage.Key));
        }

        Trace.TraceInformation(
            "[PerformanceMetrics] {0} {1} {2:F1} ms, {3} results",
            snapshot.Operation,
            snapshot.Outcome,
            snapshot.TotalMilliseconds,
            snapshot.ResultCount);
        Completed?.Invoke(null, snapshot);
    }

    private sealed class EmptyStage : IDisposable
    {
        public static EmptyStage Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}

public sealed class PerformanceMeasurement : IDisposable
{
    private readonly object _gate = new();
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Dictionary<string, double> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly Action<PerformanceMeasurement, PerformanceMetricSnapshot> _publish;
    private PerformanceMetricSnapshot? _snapshot;

    internal PerformanceMeasurement(
        string operation,
        PerformanceMeasurement? parent,
        Action<PerformanceMeasurement, PerformanceMetricSnapshot> publish)
    {
        Operation = operation;
        Parent = parent;
        _publish = publish;
    }

    public string Operation { get; }
    internal PerformanceMeasurement? Parent { get; }
    public PerformanceMetricSnapshot? Snapshot => _snapshot;

    public IDisposable MeasureStage(string stage) => new StageMeasurement(this, stage);

    public void SetCounter(string name, long value)
    {
        lock (_gate)
        {
            _counters[name] = value;
        }
    }

    public PerformanceMetricSnapshot Complete(string outcome, int resultCount = 0)
    {
        lock (_gate)
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            _snapshot = new PerformanceMetricSnapshot(
                Guid.NewGuid(),
                Operation,
                _startedAt,
                Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds,
                outcome,
                resultCount,
                new Dictionary<string, double>(_stages),
                new Dictionary<string, long>(_counters));
        }

        _publish(this, _snapshot);
        return _snapshot;
    }

    public void Dispose()
    {
        Complete("abandoned");
    }

    private void AddStage(string stage, double milliseconds)
    {
        lock (_gate)
        {
            _stages[stage] = _stages.GetValueOrDefault(stage) + milliseconds;
        }
    }

    private sealed class StageMeasurement : IDisposable
    {
        private readonly PerformanceMeasurement _owner;
        private readonly string _stage;
        private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
        private int _disposed;

        public StageMeasurement(PerformanceMeasurement owner, string stage)
        {
            _owner = owner;
            _stage = stage;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.AddStage(_stage, Stopwatch.GetElapsedTime(_startedTimestamp).TotalMilliseconds);
            }
        }
    }
}
