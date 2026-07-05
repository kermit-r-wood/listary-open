using System.Text.Json;
using System.Text.Json.Serialization;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookIpcEnvelope(int Version, string MessageType, object Payload)
{
    public const int CurrentVersion = 1;

    public static HookIpcEnvelope Command(HookJumpCommand command) =>
        new(CurrentVersion, "JumpDialogToFolder", command);

    public static HookIpcEnvelope Command(HookHealthProbe probe) =>
        new(CurrentVersion, "HealthProbe", probe);
}

public sealed record HookHealthProbe;

public sealed record HookJumpCommand(string DialogId, string FolderPath, TimeSpan Timeout)
{
    public int TimeoutMs => checked((int)Timeout.TotalMilliseconds);
}

public sealed record HookActiveDialogEvent(
    string DialogId,
    long WindowHandle,
    uint ProcessId,
    uint ThreadId,
    string Architecture,
    string ProcessName,
    string ClassName,
    string Title);

public sealed record HookCommandReply(string Status, string Message);

public static class HookIpcSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new HookIpcEnvelopeJsonConverter() }
    };

    public static string Serialize(HookIpcEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public static HookIpcEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<HookIpcEnvelope>(json, Options)
        ?? throw new InvalidOperationException("Hook IPC message was empty.");
}

internal sealed class HookIpcEnvelopeJsonConverter : JsonConverter<HookIpcEnvelope>
{
    public override HookIpcEnvelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetInt32();
        if (version != HookIpcEnvelope.CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported hook IPC version: {version}.");
        }

        var messageType = root.GetProperty("messageType").GetString()
            ?? throw new InvalidOperationException("Hook IPC messageType was missing.");
        var payloadElement = root.GetProperty("payload");
        object payload = messageType switch
        {
            "HealthProbe" => payloadElement.Deserialize<HookHealthProbe>(options) ?? new HookHealthProbe(),
            "JumpDialogToFolder" => payloadElement.Deserialize<HookJumpCommand>(options)
                ?? throw new InvalidOperationException("Hook jump command payload was empty."),
            "ActiveDialog" => payloadElement.Deserialize<HookActiveDialogEvent>(options)
                ?? throw new InvalidOperationException("Hook active dialog payload was empty."),
            "CommandReply" => payloadElement.Deserialize<HookCommandReply>(options)
                ?? throw new InvalidOperationException("Hook command reply payload was empty."),
            _ => throw new InvalidOperationException($"Unknown hook IPC message type: {messageType}.")
        };

        return new HookIpcEnvelope(version, messageType, payload);
    }

    public override void Write(Utf8JsonWriter writer, HookIpcEnvelope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        writer.WriteString("messageType", value.MessageType);
        writer.WritePropertyName("payload");
        JsonSerializer.Serialize(writer, value.Payload, value.Payload.GetType(), options);
        writer.WriteEndObject();
    }
}
