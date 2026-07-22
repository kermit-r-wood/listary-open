using System.Text.Json;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookIpcMessagesTests
{
    [Fact]
    public void SerializerIncludesClientBindingFields()
    {
        var command = new HookIpcEnvelope(
            HookIpcEnvelope.CurrentVersion,
            "HealthProbe",
            new HookHealthProbe(),
            123,
            "session-secret");

        var json = HookIpcSerializer.Serialize(command);

        Assert.Contains("\"clientProcessId\":123", json, StringComparison.Ordinal);
        Assert.Contains("\"secret\":\"session-secret\"", json, StringComparison.Ordinal);
    }

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
    public void JumpCommandSerializesTimeoutMsWithoutTimeout()
    {
        var command = HookIpcEnvelope.Command(
            new HookJumpCommand("dialog-1", "C:\\Users\\paulx", TimeSpan.FromMilliseconds(750)));

        var json = HookIpcSerializer.Serialize(command);

        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement.GetProperty("payload");
        Assert.True(payload.TryGetProperty("timeoutMs", out var timeoutMs));
        Assert.Equal(750, timeoutMs.GetInt32());
        Assert.False(payload.TryGetProperty("timeout", out _));
    }

    [Fact]
    public void NativeJumpCommandJsonDeserializesTimeoutMs()
    {
        var json = """
            {"version":1,"messageType":"JumpDialogToFolder","payload":{"dialogId":"dialog-1","folderPath":"C:\\Users\\paulx","timeoutMs":750}}
            """;

        var roundTrip = HookIpcSerializer.Deserialize(json);

        var payload = Assert.IsType<HookJumpCommand>(roundTrip.Payload);
        Assert.Equal("dialog-1", payload.DialogId);
        Assert.Equal("C:\\Users\\paulx", payload.FolderPath);
        Assert.Equal(750, payload.TimeoutMs);
        Assert.Equal(TimeSpan.FromMilliseconds(750), payload.Timeout);
    }

    [Fact]
    public void HealthProbeDeserializesToPayloadType()
    {
        var json = """{"version":1,"messageType":"HealthProbe","payload":{}}""";

        var roundTrip = HookIpcSerializer.Deserialize(json);

        Assert.IsType<HookHealthProbe>(roundTrip.Payload);
        Assert.Equal("HealthProbe", roundTrip.MessageType);
    }

    [Fact]
    public void GracefulShutdownCommandRoundTrips()
    {
        var command = HookIpcEnvelope.Command(new HookShutdownCommand());

        var roundTrip = HookIpcSerializer.Deserialize(HookIpcSerializer.Serialize(command));

        Assert.Equal("Shutdown", roundTrip.MessageType);
        Assert.IsType<HookShutdownCommand>(roundTrip.Payload);
    }

    [Fact]
    public void GetActiveDialogCommandRoundTrips()
    {
        var command = HookIpcEnvelope.Command(new HookActiveDialogQuery());

        var json = HookIpcSerializer.Serialize(command);
        var roundTrip = HookIpcSerializer.Deserialize(json);

        Assert.Equal(1, roundTrip.Version);
        Assert.Equal("GetActiveDialog", roundTrip.MessageType);
        Assert.IsType<HookActiveDialogQuery>(roundTrip.Payload);
    }

    [Fact]
    public void ActiveDialogDeserializesFields()
    {
        var json = """
            {"version":1,"messageType":"ActiveDialog","payload":{"dialogId":"dialog-1","windowHandle":123456,"processId":42,"threadId":84,"architecture":"x64","processName":"firefox.exe","className":"#32770","title":"Open","firefoxFileDialogUtility":true,"preloadConfirmedBeforeDialog":true}}
            """;

        var roundTrip = HookIpcSerializer.Deserialize(json);

        var payload = Assert.IsType<HookActiveDialogEvent>(roundTrip.Payload);
        Assert.Equal("dialog-1", payload.DialogId);
        Assert.Equal(123456, payload.WindowHandle);
        Assert.Equal(42u, payload.ProcessId);
        Assert.Equal(84u, payload.ThreadId);
        Assert.Equal("x64", payload.Architecture);
        Assert.Equal("firefox.exe", payload.ProcessName);
        Assert.Equal("#32770", payload.ClassName);
        Assert.Equal("Open", payload.Title);
        Assert.True(payload.FirefoxFileDialogUtility);
        Assert.True(payload.PreloadConfirmedBeforeDialog);
    }

    [Fact]
    public void ActiveDialogWithoutFirefoxUtilityProofFieldIsRejected()
    {
        var json = """
            {"version":1,"messageType":"ActiveDialog","payload":{"dialogId":"dialog-1","windowHandle":123456,"processId":42,"threadId":84,"architecture":"x64","processName":"firefox.exe","className":"#32770","title":"Open","preloadConfirmedBeforeDialog":true}}
            """;

        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.Contains("firefoxFileDialogUtility", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveDialogWithoutPrecaptureTimingProofFieldIsRejected()
    {
        var json = """
            {"version":1,"messageType":"ActiveDialog","payload":{"dialogId":"dialog-1","windowHandle":123456,"processId":42,"threadId":84,"architecture":"x64","processName":"firefox.exe","className":"#32770","title":"Open","firefoxFileDialogUtility":true}}
            """;

        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.Contains("preloadConfirmedBeforeDialog", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommandReplyDeserializesFields()
    {
        var json = """{"version":1,"messageType":"CommandReply","payload":{"status":"ok","message":"Jumped"}}""";

        var roundTrip = HookIpcSerializer.Deserialize(json);

        var payload = Assert.IsType<HookCommandReply>(roundTrip.Payload);
        Assert.Equal("ok", payload.Status);
        Assert.Equal("Jumped", payload.Message);
    }

    [Fact]
    public void UnknownVersionIsRejected()
    {
        var json = """{"version":99,"messageType":"HealthProbe","payload":{}}""";

        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.Contains("Unsupported hook IPC version", exception.Message);
    }

    [Fact]
    public void UnknownMessageTypeIsRejected()
    {
        var json = """{"version":1,"messageType":"Unexpected","payload":{}}""";

        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.Contains("Unknown hook IPC message type", exception.Message);
    }

    [Theory]
    [InlineData("{\"messageType\":\"HealthProbe\",\"payload\":{}}")]
    [InlineData("{\"version\":1,\"payload\":{}}")]
    [InlineData("{\"version\":1,\"messageType\":\"HealthProbe\"}")]
    public void MissingEnvelopePropertyIsRejectedAsInvalidMessage(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.StartsWith("Invalid hook IPC message:", exception.Message);
    }

    [Theory]
    [InlineData("{\"version\":1,\"messageType\":\"JumpDialogToFolder\",\"payload\":{\"dialogId\":\"dialog-1\",\"folderPath\":\"C:\\\\Users\\\\paulx\"}}")]
    [InlineData("{\"version\":1,\"messageType\":\"JumpDialogToFolder\",\"payload\":{\"folderPath\":\"C:\\\\Users\\\\paulx\",\"timeoutMs\":750}}")]
    [InlineData("{\"version\":1,\"messageType\":\"ActiveDialog\",\"payload\":{}}")]
    [InlineData("{\"version\":1,\"messageType\":\"CommandReply\",\"payload\":{}}")]
    [InlineData("{\"version\":1,\"messageType\":\"JumpDialogToFolder\",\"payload\":null}")]
    [InlineData("{\"version\":1,\"messageType\":\"ActiveDialog\",\"payload\":null}")]
    [InlineData("{\"version\":1,\"messageType\":\"CommandReply\",\"payload\":null}")]
    public void InvalidPayloadIsRejectedAsInvalidMessage(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => HookIpcSerializer.Deserialize(json));

        Assert.StartsWith("Invalid hook IPC message:", exception.Message);
    }

    [Theory]
    [InlineData("", "C:\\Users\\paulx", 750)]
    [InlineData("dialog-1", "", 750)]
    [InlineData("dialog-1", "C:\\Users\\paulx", -1)]
    public void JumpCommandRejectsInvalidValues(string dialogId, string folderPath, int timeoutMs)
    {
        Assert.IsAssignableFrom<ArgumentException>(
            Assert.ThrowsAny<ArgumentException>(
                () => new HookJumpCommand(dialogId, folderPath, TimeSpan.FromMilliseconds(timeoutMs))));
    }
}
