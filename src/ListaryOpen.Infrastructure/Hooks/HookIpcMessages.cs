using System.Text.Json;
using System.Text.Json.Serialization;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookIpcEnvelope(
    int Version,
    string MessageType,
    object Payload,
    int ClientProcessId = 0,
    string Secret = "")
{
    public const int CurrentVersion = 1;

    public static HookIpcEnvelope Command(HookJumpCommand command) =>
        new(CurrentVersion, "JumpDialogToFolder", command);

    public static HookIpcEnvelope Command(HookHealthProbe probe) =>
        new(CurrentVersion, "HealthProbe", probe);

    public static HookIpcEnvelope Command(HookActiveDialogQuery query) =>
        new(CurrentVersion, "GetActiveDialog", query);
}

public sealed record HookHealthProbe;

public sealed record HookActiveDialogQuery;

public sealed record HookJumpCommand
{
    public HookJumpCommand(string dialogId, string folderPath, TimeSpan timeout)
        : this(dialogId, folderPath, checked((int)timeout.TotalMilliseconds))
    {
    }

    [JsonConstructor]
    public HookJumpCommand(string dialogId, string folderPath, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (timeoutMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Hook jump command timeout must be nonnegative.");
        }

        DialogId = dialogId;
        FolderPath = folderPath;
        TimeoutMs = timeoutMs;
    }

    public string DialogId { get; }
    public string FolderPath { get; }
    public int TimeoutMs { get; }

    [JsonIgnore]
    public TimeSpan Timeout => TimeSpan.FromMilliseconds(TimeoutMs);
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
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw InvalidMessage("root must be an object");
        }

        var version = GetRequiredInt32(root, "version");
        if (version != HookIpcEnvelope.CurrentVersion)
        {
            throw new InvalidOperationException($"Unsupported hook IPC version: {version}.");
        }

        var messageType = GetRequiredString(root, "messageType");
        var payloadElement = GetRequiredObject(root, "payload");
        object payload = messageType switch
        {
            "HealthProbe" => new HookHealthProbe(),
            "GetActiveDialog" => new HookActiveDialogQuery(),
            "JumpDialogToFolder" => ReadJumpCommand(payloadElement),
            "ActiveDialog" => ReadActiveDialogEvent(payloadElement),
            "CommandReply" => ReadCommandReply(payloadElement),
            _ => throw new InvalidOperationException($"Unknown hook IPC message type: {messageType}.")
        };

        var clientProcessId = root.TryGetProperty("clientProcessId", out var clientProcessIdElement)
            && clientProcessIdElement.TryGetInt32(out var parsedClientProcessId)
            ? parsedClientProcessId
            : 0;
        var secret = root.TryGetProperty("secret", out var secretElement)
            && secretElement.ValueKind == JsonValueKind.String
            ? secretElement.GetString() ?? string.Empty
            : string.Empty;

        return new HookIpcEnvelope(version, messageType, payload, clientProcessId, secret);
    }

    public override void Write(Utf8JsonWriter writer, HookIpcEnvelope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", value.Version);
        writer.WriteString("messageType", value.MessageType);
        writer.WriteNumber("clientProcessId", value.ClientProcessId);
        writer.WriteString("secret", value.Secret);
        writer.WritePropertyName("payload");
        JsonSerializer.Serialize(writer, value.Payload, value.Payload.GetType(), options);
        writer.WriteEndObject();
    }

    private static JsonElement GetRequiredProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            throw InvalidMessage($"{propertyName} was missing");
        }

        return property;
    }

    private static JsonElement GetRequiredObject(JsonElement root, string propertyName)
    {
        var property = GetRequiredProperty(root, propertyName);
        if (property.ValueKind != JsonValueKind.Object)
        {
            throw InvalidMessage($"{propertyName} must be an object");
        }

        return property;
    }

    private static int GetRequiredInt32(JsonElement root, string propertyName)
    {
        var property = GetRequiredProperty(root, propertyName);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw InvalidMessage($"{propertyName} was invalid");
        }

        return value;
    }

    private static long GetRequiredInt64(JsonElement root, string propertyName)
    {
        var property = GetRequiredProperty(root, propertyName);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
        {
            throw InvalidMessage($"{propertyName} was invalid");
        }

        return value;
    }

    private static uint GetRequiredUInt32(JsonElement root, string propertyName)
    {
        var property = GetRequiredProperty(root, propertyName);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetUInt32(out var value))
        {
            throw InvalidMessage($"{propertyName} was invalid");
        }

        return value;
    }

    private static string GetRequiredString(JsonElement root, string propertyName)
    {
        var property = GetRequiredProperty(root, propertyName);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? throw InvalidMessage($"{propertyName} was invalid")
            : throw InvalidMessage($"{propertyName} was invalid");
    }

    private static HookJumpCommand ReadJumpCommand(JsonElement payload)
    {
        try
        {
            return new HookJumpCommand(
                GetRequiredString(payload, "dialogId"),
                GetRequiredString(payload, "folderPath"),
                GetRequiredInt32(payload, "timeoutMs"));
        }
        catch (ArgumentException exception)
        {
            throw InvalidMessage(exception.Message);
        }
    }

    private static HookActiveDialogEvent ReadActiveDialogEvent(JsonElement payload) =>
        new(
            GetRequiredString(payload, "dialogId"),
            GetRequiredInt64(payload, "windowHandle"),
            GetRequiredUInt32(payload, "processId"),
            GetRequiredUInt32(payload, "threadId"),
            GetRequiredString(payload, "architecture"),
            GetRequiredString(payload, "processName"),
            GetRequiredString(payload, "className"),
            GetRequiredString(payload, "title"));

    private static HookCommandReply ReadCommandReply(JsonElement payload) =>
        new(
            GetRequiredString(payload, "status"),
            GetRequiredString(payload, "message"));

    private static InvalidOperationException InvalidMessage(string reason) =>
        new($"Invalid hook IPC message: {reason}.");
}
