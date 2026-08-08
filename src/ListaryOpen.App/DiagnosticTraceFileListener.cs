using System.Diagnostics;
using System.IO;
using System.Text;

namespace ListaryOpen.App;

/// <summary>
/// Append-only application diagnostics with bounded startup rotation. The file
/// remains readable while ListaryOpen is running so indexing failures can be
/// inspected without stopping the application.
/// </summary>
internal sealed class DiagnosticTraceFileListener : TextWriterTraceListener
{
    private const long DefaultMaximumFileSize = 5 * 1024 * 1024;
    private const int DefaultRetainedFileCount = 2;

    public DiagnosticTraceFileListener(string path)
        : this(path, DefaultMaximumFileSize, DefaultRetainedFileCount)
    {
    }

    internal DiagnosticTraceFileListener(
        string path,
        long maximumFileSize,
        int retainedFileCount)
        : base(CreateWriter(path, maximumFileSize, retainedFileCount), "ListaryOpenFile")
    {
        TraceOutputOptions = TraceOptions.DateTime | TraceOptions.ProcessId | TraceOptions.ThreadId;
    }

    private static TextWriter CreateWriter(
        string path,
        long maximumFileSize,
        int retainedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileSize);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFileCount);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        RotateIfNeeded(path, maximumFileSize, retainedFileCount);
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 16 * 1024,
            options: FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
    }

    private static void RotateIfNeeded(string path, long maximumFileSize, int retainedFileCount)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < maximumFileSize)
        {
            return;
        }

        if (retainedFileCount == 0)
        {
            File.Delete(path);
            return;
        }

        for (var index = retainedFileCount; index >= 1; index--)
        {
            var destination = $"{path}.{index}";
            var source = index == 1 ? path : $"{path}.{index - 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }
    }
}
