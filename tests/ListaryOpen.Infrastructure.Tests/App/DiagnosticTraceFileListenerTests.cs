using System.Diagnostics;
using ListaryOpen.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class DiagnosticTraceFileListenerTests
{
    [Fact]
    public void DiagnosticFileIsReadableWhileOpenAndContainsTraceMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("listary-open-diagnostics-");
        var path = Path.Combine(directory.FullName, "listaryopen.log");
        try
        {
            using var listener = new DiagnosticTraceFileListener(path);
            listener.TraceEvent(
                new TraceEventCache(),
                "ListaryOpen",
                TraceEventType.Error,
                id: 0,
                "Index failure diagnostic marker");
            listener.Flush();

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var contents = reader.ReadToEnd();
            Assert.Contains("Index failure diagnostic marker", contents, StringComparison.Ordinal);
            Assert.Contains("ProcessId=", contents, StringComparison.Ordinal);
            Assert.Contains("ThreadId=", contents, StringComparison.Ordinal);
            Assert.Contains("DateTime=", contents, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void OversizedDiagnosticFileRotatesAtStartup()
    {
        var directory = Directory.CreateTempSubdirectory("listary-open-diagnostics-rotate-");
        var path = Path.Combine(directory.FullName, "listaryopen.log");
        try
        {
            File.WriteAllText(path, new string('x', 128));
            File.WriteAllText(path + ".1", "previous generation");

            using (var listener = new DiagnosticTraceFileListener(
                       path,
                       maximumFileSize: 64,
                       retainedFileCount: 2))
            {
                listener.WriteLine("new generation");
                listener.Flush();
            }

            Assert.Equal(new string('x', 128), File.ReadAllText(path + ".1"));
            Assert.Equal("previous generation", File.ReadAllText(path + ".2"));
            Assert.Contains("new generation", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
