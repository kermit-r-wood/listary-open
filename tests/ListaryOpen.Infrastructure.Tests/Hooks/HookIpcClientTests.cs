using System.IO;
using System.IO.Pipes;
using System.Text;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookIpcClientTests
{
    private static readonly UTF8Encoding PipeEncoding = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task MissingPipeReturnsHostUnavailableOrTimeout()
    {
        var client = new HookIpcClient("listary-open-missing-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));

        var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);

        Assert.Contains(result.Status, new[] { HookJumpStatus.HostUnavailable, HookJumpStatus.Timeout });
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
        var serverTask = ServeOnceAsync(
            pipeName,
            HookIpcSerializer.Serialize(new HookIpcEnvelope(
                HookIpcEnvelope.CurrentVersion,
                "CommandReply",
                new HookCommandReply("NoActiveDialog", "No active hook dialog."))),
            CancellationToken.None);
        var client = new HookIpcClient(pipeName, TimeSpan.FromSeconds(2));

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

        var result = await clientTask.WaitAsync(TimeSpan.FromSeconds(3));
        if (result.Status != HookJumpStatus.NoActiveDialog)
        {
            await serverCancellation.CancelAsync();
            await IgnoreCanceledAsync(serverTask);
        }

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
        _ = await serverTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task InvalidJsonReplyReturnsFailed()
    {
        var pipeName = "listary-open-invalid-json-" + Guid.NewGuid();
        var serverTask = ServeOnceAsync(pipeName, "{not valid json", CancellationToken.None);
        var client = new HookIpcClient(pipeName, TimeSpan.FromSeconds(2));

        var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);
        _ = await serverTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(HookJumpStatus.Failed, result.Status);
        Assert.Contains("invalid JSON", result.Message, StringComparison.OrdinalIgnoreCase);
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

    private static async Task IgnoreCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record ServerExchange(string Request);
}
