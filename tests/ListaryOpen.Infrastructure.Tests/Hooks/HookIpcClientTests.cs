using System.IO;
using System.IO.Pipes;
using System.Text;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookIpcClientTests
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task MissingPipeReturnsHostUnavailable()
    {
        var client = new HookIpcClient("listary-open-missing-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));

        var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.HostUnavailable, result.Status);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var client = new HookIpcClient("listary-open-missing-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", cancellation.Token));
    }

    [Fact]
    public async Task CommandReplyRoundTripsOverNamedPipe()
    {
        var pipeName = "listary-open-test-" + Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = ServeOnceAsync(
            pipeName,
            HookIpcSerializer.Serialize(new HookIpcEnvelope(
                HookIpcEnvelope.CurrentVersion,
                "CommandReply",
                new HookCommandReply("NoActiveDialog", "No active hook dialog."))),
            serverCancellation.Token);
        var client = new HookIpcClient(pipeName, TimeSpan.FromSeconds(2));

        try
        {
            var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);
            var exchange = await serverTask.WaitAsync(TimeSpan.FromSeconds(1));

            var request = HookIpcSerializer.Deserialize(exchange.Request);
            var payload = Assert.IsType<HookJumpCommand>(request.Payload);
            Assert.Equal("JumpDialogToFolder", request.MessageType);
            Assert.Equal("dlg", payload.DialogId);
            Assert.Equal("C:\\Users\\paulx", payload.FolderPath);
            Assert.Equal(750, payload.TimeoutMs);
            Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
            Assert.Equal("No active hook dialog.", result.Message);
        }
        finally
        {
            await StopServerAsync(serverCancellation, serverTask);
        }
    }

    [Fact]
    public async Task ConnectWaitsForServerCreatedWithinTimeout()
    {
        var pipeName = "listary-open-delayed-" + Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<ServerExchange>? serverTask = null;
        var client = new HookIpcClient(pipeName, TimeSpan.FromSeconds(2));

        var clientTask = client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);
        await Task.Delay(50);
        serverTask = ServeOnceAsync(
            pipeName,
            HookIpcSerializer.Serialize(new HookIpcEnvelope(
                HookIpcEnvelope.CurrentVersion,
                "CommandReply",
                new HookCommandReply("NoActiveDialog", "No active hook dialog."))),
            serverCancellation.Token);

        try
        {
            var result = await clientTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
            _ = await serverTask.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await StopServerAsync(serverCancellation, serverTask);
        }
    }

    [Fact]
    public async Task ConnectedHostWithoutReplyReturnsTimeout()
    {
        var pipeName = "listary-open-no-reply-" + Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = ServeWithoutReplyOnceAsync(pipeName, serverCancellation.Token);
        var client = new HookIpcClient(pipeName, TimeSpan.FromMilliseconds(100));

        try
        {
            var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);

            Assert.Equal(HookJumpStatus.Timeout, result.Status);
        }
        finally
        {
            await StopServerAsync(serverCancellation, serverTask);
        }
    }

    [Fact]
    public async Task InvalidJsonReplyReturnsFailed()
    {
        var pipeName = "listary-open-invalid-json-" + Guid.NewGuid();
        using var serverCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = ServeOnceAsync(pipeName, "{not valid json", serverCancellation.Token);
        var client = new HookIpcClient(pipeName, TimeSpan.FromSeconds(2));

        try
        {
            var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);
            _ = await serverTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(HookJumpStatus.Failed, result.Status);
            Assert.Contains("invalid JSON", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await StopServerAsync(serverCancellation, serverTask);
        }
    }

    private static async Task<ServerExchange> ServeOnceAsync(
        string pipeName,
        string response,
        CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(cancellationToken);

        var request = await ReadLineAsync(server, cancellationToken);
        await WriteLineAsync(server, response, cancellationToken);

        return new ServerExchange(request ?? string.Empty);
    }

    private static async Task ServeWithoutReplyOnceAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        await server.WaitForConnectionAsync(cancellationToken);
        _ = await ReadLineAsync(server, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var buffer = new byte[256];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            }

            for (var index = 0; index < bytesRead; index++)
            {
                if (buffer[index] == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.UTF8.GetString(line.ToArray());
                }

                line.Add(buffer[index]);
            }
        }
    }

    private static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        var response = PipeEncoding.GetBytes(line + "\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task StopServerAsync(CancellationTokenSource cancellation, Task? task)
    {
        await cancellation.CancelAsync();
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private sealed record ServerExchange(string Request);
}
