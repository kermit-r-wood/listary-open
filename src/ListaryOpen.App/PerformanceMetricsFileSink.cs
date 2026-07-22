using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using ListaryOpen.Core.Search;

namespace ListaryOpen.App;

internal sealed class PerformanceMetricsFileSink : IDisposable
{
    private static readonly TimeSpan DefaultBatchDelay = TimeSpan.FromMilliseconds(200);
    private const int MaximumBatchSize = 128;
    private const long DefaultMaximumFileSize = 10 * 1024 * 1024;
    private const int DefaultRetainedFileCount = 3;

    private readonly Channel<PerformanceMetricSnapshot> _pendingMetrics;
    private readonly string _path;
    private readonly long _maximumFileSize;
    private readonly int _retainedFileCount;
    private readonly TimeSpan _batchDelay;
    private StreamWriter _writer;
    private readonly Task _writerTask;
    private int _disposed;
    private long _droppedMetricCount;

    public PerformanceMetricsFileSink(string path)
        : this(path, DefaultMaximumFileSize, DefaultRetainedFileCount, DefaultBatchDelay)
    {
    }

    internal PerformanceMetricsFileSink(
        string path,
        long maximumFileSize,
        int retainedFileCount,
        TimeSpan batchDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);
        if (batchDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchDelay));
        }
        _path = path;
        _maximumFileSize = maximumFileSize;
        _retainedFileCount = retainedFileCount;
        _batchDelay = batchDelay;
        _writer = OpenWriter(path);
        _pendingMetrics = Channel.CreateBounded<PerformanceMetricSnapshot>(
            new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        _writerTask = Task.Run(WritePendingMetricsAsync);
        PerformanceMetrics.Completed += OnMetricCompleted;
    }

    private void OnMetricCompleted(object? sender, PerformanceMetricSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            if (!_pendingMetrics.Writer.TryWrite(snapshot))
            {
                Interlocked.Increment(ref _droppedMetricCount);
            }
        }
    }

    internal long DroppedMetricCount => Interlocked.Read(ref _droppedMetricCount);

    private async Task WritePendingMetricsAsync()
    {
        try
        {
            while (await _pendingMetrics.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (_batchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_batchDelay).ConfigureAwait(false);
                }

                if (_writer.BaseStream.Length >= _maximumFileSize)
                {
                    await RotateAsync().ConfigureAwait(false);
                }

                var written = 0;
                while (written < MaximumBatchSize && _pendingMetrics.Reader.TryRead(out var snapshot))
                {
                    await _writer.WriteLineAsync(snapshot.ToJson()).ConfigureAwait(false);
                    written++;
                }

                await _writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException exception)
        {
            Trace.TraceWarning("Could not write performance metrics: {0}", exception.Message);
        }
        finally
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RotateAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
        if (_retainedFileCount == 0)
        {
            File.Delete(_path);
            _writer = OpenWriter(_path);
            return;
        }

        for (var index = _retainedFileCount; index >= 1; index--)
        {
            var destination = $"{_path}.{index}";
            var source = index == 1 ? _path : $"{_path}.{index - 1}";
            if (!File.Exists(source))
            {
                continue;
            }

            File.Move(source, destination, overwrite: true);
        }

        _writer = OpenWriter(_path);
    }

    private static StreamWriter OpenWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            useAsync: true);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PerformanceMetrics.Completed -= OnMetricCompleted;
        _pendingMetrics.Writer.TryComplete();
        try
        {
            _writerTask.GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Trace.TraceWarning("Could not close performance metrics: {0}", exception.Message);
        }
    }
}
