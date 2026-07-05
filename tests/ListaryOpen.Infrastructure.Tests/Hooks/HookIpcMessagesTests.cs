using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookIpcMessagesTests
{
    [Fact]
    public void JumpCommandRoundTripsWithVersionAndFolder()
    {
        var command = HookIpcEnvelope.Command(
            new HookJumpCommand("dialog-1", "C:\\Users\\paulx", TimeSpan.FromMilliseconds(750)));

        var json = HookIpcSerializer.Serialize(command);
        var roundTrip = HookIpcSerializer.Deserialize(json);

        var payload = Assert.IsType<HookJumpCommand>(roundTrip.Payload);
        Assert.Equal(1, roundTrip.Version);
        Assert.Equal("JumpDialogToFolder", roundTrip.MessageType);
        Assert.Equal("dialog-1", payload.DialogId);
        Assert.Equal("C:\\Users\\paulx", payload.FolderPath);
        Assert.Equal(750, payload.TimeoutMs);
    }

    [Fact]
    public void UnknownVersionIsRejected()
    {
        var json = """{"version":99,"messageType":"HealthProbe","payload":{}}""";

        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.Contains("Unsupported hook IPC version", exception.Message);
    }
}
