# Hook Quick Switch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace quick switch's primary dialog control path with a 32-bit and 64-bit hook bridge that can detect and quick-switch the currently open Antigravity, Notepad++, Chrome, Firefox, and MobaXterm file dialogs.

**Architecture:** Keep ListaryOpen.App as the managed controller for hotkeys, search, settings, and folder candidate selection. Add a managed hook bridge in Infrastructure that communicates over named pipes with architecture-specific native hook hosts. Add x64 and x86 native hook host/DLL outputs that install Win32 hooks into matching-bitness dialog threads, report active dialog context, and execute folder-jump commands without UIA or visible keyboard typing.

**Tech Stack:** .NET 8 WPF, xUnit, Win32 P/Invoke, named pipes, Rust native `cdylib`/host binaries targeting `x86_64-pc-windows-msvc` and `i686-pc-windows-msvc`, PowerShell verification scripts.

---

## File Structure

Create managed hook bridge files:

- `src/ListaryOpen.Infrastructure/Hooks/HookArchitecture.cs`: `X64`/`X86` enum and helpers.
- `src/ListaryOpen.Infrastructure/Hooks/HookProcessSnapshot.cs`: process id, thread id, executable name, and bitness snapshot.
- `src/ListaryOpen.Infrastructure/Hooks/HookDialogContext.cs`: stable managed representation of a hook-detected dialog.
- `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchStatus.cs`: per-architecture health and UI status text.
- `src/ListaryOpen.Infrastructure/Hooks/HookJumpResult.cs`: hook-level jump result mapped to `DialogJumpResult`.
- `src/ListaryOpen.Infrastructure/Hooks/IHookQuickSwitchBridge.cs`: app-facing hook bridge interface.
- `src/ListaryOpen.Infrastructure/Hooks/HookHostPaths.cs`: resolves `hooks\x64` and `hooks\x86` under `AppContext.BaseDirectory`.
- `src/ListaryOpen.Infrastructure/Hooks/HookHostProcessFactory.cs`: starts hook hosts, including elevated launch mode.
- `src/ListaryOpen.Infrastructure/Hooks/HookProcessBitnessDetector.cs`: maps dialog process id to hook architecture.
- `src/ListaryOpen.Infrastructure/Hooks/HookIpcMessages.cs`: versioned IPC command/event records.
- `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`: named-pipe client with health probe, command timeout, and event reader.
- `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`: bridge orchestration, status updates, and fallback boundary.

Modify managed app files:

- `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`: try hook bridge before `IDialogAutomation`.
- `src/ListaryOpen.Infrastructure/Dialog/IDialogAutomation.cs`: keep as external fallback contract; do not add UIA.
- `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs`: add process bitness and module inspection P/Invokes used by the managed bridge/tests.
- `src/ListaryOpen.App/App.xaml.cs`: create and dispose the hook bridge; pass it into `DialogBridge`; expose hook enable action to Settings.
- `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`: add hook enable command and status text.
- `src/ListaryOpen.App/MainWindow.xaml`: add Hook quick switch status and Enable button.
- `src/ListaryOpen.App/ListaryOpen.App.csproj`: copy hook host/DLL outputs to `hooks\x64` and `hooks\x86`.

Create native hook workspace:

- `native/ListaryOpen.Hooks/Cargo.toml`: Rust workspace.
- `native/ListaryOpen.Hooks/hook_common/Cargo.toml`: shared constants and protocol helpers.
- `native/ListaryOpen.Hooks/hook_common/src/lib.rs`: magic constants, message ids, and UTF-16 helpers.
- `native/ListaryOpen.Hooks/hook_host/Cargo.toml`: native host executable.
- `native/ListaryOpen.Hooks/hook_host/src/main.rs`: pipe server, dialog discovery, SetWindowsHookEx orchestration, command routing.
- `native/ListaryOpen.Hooks/hook_dll/Cargo.toml`: hook DLL.
- `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`: exported hook proc, dialog event reporting, jump command handler.
- `native/ListaryOpen.Hooks/.cargo/config.toml`: target output configuration.

Create tests and verification:

- `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookArchitectureTests.cs`
- `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostPathsTests.cs`
- `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookProcessBitnessDetectorTests.cs`
- `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcMessagesTests.cs`
- `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs`
- `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeHookTests.cs`
- `tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs`
- `tools/verify-hook-modules.ps1`: checks hook DLLs loaded into named target processes.
- `docs/manual-hook-quick-switch-test.md`: acceptance checklist for Antigravity, Notepad++, Chrome, Firefox, and MobaXterm.

## Task 1: Managed Hook Contracts

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookArchitecture.cs`
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookDialogContext.cs`
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchStatus.cs`
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookJumpResult.cs`
- Create: `src/ListaryOpen.Infrastructure/Hooks/IHookQuickSwitchBridge.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookArchitectureTests.cs`

- [ ] **Step 1: Write failing architecture tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookArchitectureTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookArchitectureTests
{
    [Theory]
    [InlineData(HookArchitecture.X64, "x64")]
    [InlineData(HookArchitecture.X86, "x86")]
    public void FolderNameReturnsPackagingFolder(HookArchitecture architecture, string expected)
    {
        Assert.Equal(expected, architecture.ToFolderName());
    }

    [Theory]
    [InlineData(true, HookArchitecture.X64)]
    [InlineData(false, HookArchitecture.X86)]
    public void FromIs64BitMapsToArchitecture(bool is64Bit, HookArchitecture expected)
    {
        Assert.Equal(expected, HookArchitectureExtensions.FromIs64Bit(is64Bit));
    }
}
```

- [ ] **Step 2: Run the failing test**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter HookArchitectureTests -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-contracts\
```

Expected: build fails because `ListaryOpen.Infrastructure.Hooks` types do not exist.

- [ ] **Step 3: Add hook contract files**

Create `src/ListaryOpen.Infrastructure/Hooks/HookArchitecture.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public enum HookArchitecture
{
    X64,
    X86
}

public static class HookArchitectureExtensions
{
    public static HookArchitecture FromIs64Bit(bool is64Bit) =>
        is64Bit ? HookArchitecture.X64 : HookArchitecture.X86;

    public static string ToFolderName(this HookArchitecture architecture) =>
        architecture switch
        {
            HookArchitecture.X64 => "x64",
            HookArchitecture.X86 => "x86",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture), architecture, "Unknown hook architecture.")
        };
}
```

Create `src/ListaryOpen.Infrastructure/Hooks/HookDialogContext.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookDialogContext(
    string DialogId,
    IntPtr WindowHandle,
    uint ProcessId,
    uint ThreadId,
    HookArchitecture Architecture,
    string ProcessName,
    string ClassName,
    string Title,
    DateTimeOffset ObservedAt);
```

Create `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchStatus.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public sealed record HookArchitectureStatus(
    HookArchitecture Architecture,
    bool Enabled,
    bool HostRunning,
    bool HookDllPresent,
    string Message);

public sealed record HookQuickSwitchStatus(
    bool Enabled,
    HookArchitectureStatus X64,
    HookArchitectureStatus X86)
{
    public string DisplayText
    {
        get
        {
            if (!Enabled)
            {
                return "Hook quick switch: disabled";
            }

            if (X64.HostRunning && X86.HostRunning)
            {
                return "Hook quick switch: enabled for x64 and x86";
            }

            if (X64.HostRunning || X86.HostRunning)
            {
                return $"Hook quick switch: partial ({X64.Message}; {X86.Message})";
            }

            return $"Hook quick switch: unavailable ({X64.Message}; {X86.Message})";
        }
    }

    public static HookQuickSwitchStatus Disabled() => new(
        false,
        new HookArchitectureStatus(HookArchitecture.X64, false, false, false, "x64 disabled"),
        new HookArchitectureStatus(HookArchitecture.X86, false, false, false, "x86 disabled"));
}
```

Create `src/ListaryOpen.Infrastructure/Hooks/HookJumpResult.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public enum HookJumpStatus
{
    Success,
    NoActiveDialog,
    UnsupportedDialog,
    HostUnavailable,
    AccessDenied,
    Timeout,
    TargetGone,
    Failed
}

public sealed record HookJumpResult(HookJumpStatus Status, string Message)
{
    public bool Succeeded => Status == HookJumpStatus.Success;

    public static HookJumpResult Success(string message) => new(HookJumpStatus.Success, message);
}
```

Create `src/ListaryOpen.Infrastructure/Hooks/IHookQuickSwitchBridge.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public interface IHookQuickSwitchBridge : IDisposable
{
    HookQuickSwitchStatus Status { get; }

    event EventHandler<HookQuickSwitchStatus>? StatusChanged;

    Task EnableAsync(CancellationToken cancellationToken);

    Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken);

    Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Run contract tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter HookArchitectureTests -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-contracts\
```

Expected: `HookArchitectureTests` passes.

- [ ] **Step 5: Commit**

```powershell
git add src/ListaryOpen.Infrastructure/Hooks tests/ListaryOpen.Infrastructure.Tests/Hooks/HookArchitectureTests.cs
git commit -m "feat: add hook quick switch contracts"
```

## Task 2: Hook Host Paths And Process Bitness

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookHostPaths.cs`
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookProcessBitnessDetector.cs`
- Modify: `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostPathsTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookProcessBitnessDetectorTests.cs`

- [ ] **Step 1: Write failing path tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostPathsTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookHostPathsTests
{
    [Fact]
    public void ForArchitectureUsesProgramDirectoryHooksSubfolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "listary-open-hook-paths-" + Guid.NewGuid());
        var paths = new HookHostPaths(root);

        var x64 = paths.ForArchitecture(HookArchitecture.X64);
        var x86 = paths.ForArchitecture(HookArchitecture.X86);

        Assert.Equal(Path.Combine(root, "hooks", "x64", "ListaryOpen.HookHost.exe"), x64.HostExePath);
        Assert.Equal(Path.Combine(root, "hooks", "x64", "ListaryOpen.Hook.dll"), x64.HookDllPath);
        Assert.Equal(Path.Combine(root, "hooks", "x86", "ListaryOpen.HookHost.exe"), x86.HostExePath);
        Assert.Equal(Path.Combine(root, "hooks", "x86", "ListaryOpen.Hook.dll"), x86.HookDllPath);
    }

    [Fact]
    public void SnapshotReportsDllPresenceSeparatelyFromHostPresence()
    {
        var root = Directory.CreateTempSubdirectory("listary-open-hook-paths-");
        try
        {
            var x64Folder = Directory.CreateDirectory(Path.Combine(root.FullName, "hooks", "x64"));
            File.WriteAllText(Path.Combine(x64Folder.FullName, "ListaryOpen.Hook.dll"), string.Empty);
            var paths = new HookHostPaths(root.FullName);

            var snapshot = paths.ForArchitecture(HookArchitecture.X64).Snapshot();

            Assert.False(snapshot.HostExists);
            Assert.True(snapshot.HookDllExists);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Write failing bitness tests with injected native probe**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookProcessBitnessDetectorTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookProcessBitnessDetectorTests
{
    [Theory]
    [InlineData(true, HookArchitecture.X64)]
    [InlineData(false, HookArchitecture.X86)]
    public void DetectFromProbeResultMapsArchitecture(bool is64BitProcess, HookArchitecture expected)
    {
        var detector = new HookProcessBitnessDetector(_ => is64BitProcess);

        Assert.Equal(expected, detector.GetArchitectureForProcess(1234));
    }

    [Fact]
    public void DetectFromProbeWrapsNativeFailure()
    {
        var detector = new HookProcessBitnessDetector(_ => throw new InvalidOperationException("OpenProcess failed."));

        var exception = Assert.Throws<InvalidOperationException>(() => detector.GetArchitectureForProcess(1234));

        Assert.Contains("OpenProcess failed", exception.Message);
    }
}
```

- [ ] **Step 3: Run failing tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "HookHostPathsTests|HookProcessBitnessDetectorTests" -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-paths\
```

Expected: build fails because path and bitness classes do not exist.

- [ ] **Step 4: Implement path and bitness classes**

Create `src/ListaryOpen.Infrastructure/Hooks/HookHostPaths.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookHostPaths
{
    private readonly string _programDirectory;

    public HookHostPaths(string programDirectory)
    {
        if (string.IsNullOrWhiteSpace(programDirectory))
        {
            throw new ArgumentException("Program directory cannot be empty.", nameof(programDirectory));
        }

        _programDirectory = Path.GetFullPath(programDirectory);
    }

    public static HookHostPaths CreateDefault() => new(AppContext.BaseDirectory);

    public HookArchitecturePaths ForArchitecture(HookArchitecture architecture)
    {
        var folder = Path.Combine(_programDirectory, "hooks", architecture.ToFolderName());
        return new HookArchitecturePaths(
            architecture,
            folder,
            Path.Combine(folder, "ListaryOpen.HookHost.exe"),
            Path.Combine(folder, "ListaryOpen.Hook.dll"));
    }
}

public sealed record HookArchitecturePaths(
    HookArchitecture Architecture,
    string DirectoryPath,
    string HostExePath,
    string HookDllPath)
{
    public HookArchitecturePathSnapshot Snapshot() => new(
        Architecture,
        File.Exists(HostExePath),
        File.Exists(HookDllPath),
        HostExePath,
        HookDllPath);
}

public sealed record HookArchitecturePathSnapshot(
    HookArchitecture Architecture,
    bool HostExists,
    bool HookDllExists,
    string HostExePath,
    string HookDllPath);
```

Create `src/ListaryOpen.Infrastructure/Hooks/HookProcessBitnessDetector.cs`:

```csharp
using ListaryOpen.Infrastructure.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookProcessBitnessDetector
{
    private readonly Func<uint, bool> _is64BitProcess;

    public HookProcessBitnessDetector()
        : this(Is64BitProcess)
    {
    }

    internal HookProcessBitnessDetector(Func<uint, bool> is64BitProcess)
    {
        _is64BitProcess = is64BitProcess;
    }

    public HookArchitecture GetArchitectureForProcess(uint processId) =>
        HookArchitectureExtensions.FromIs64Bit(_is64BitProcess(processId));

    private static bool Is64BitProcess(uint processId)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return false;
        }

        using var process = Process.GetProcessById(checked((int)processId));
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenProcess failed for process {processId}. Win32 error: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            if (!NativeMethods.IsWow64Process2(handle, out var processMachine, out _))
            {
                throw new InvalidOperationException($"IsWow64Process2 failed for process {processId}. Win32 error: {Marshal.GetLastWin32Error()}.");
            }

            return processMachine == NativeMethods.ImageFileMachineUnknown;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
```

Modify `src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs` by adding constants and P/Invokes:

```csharp
internal const uint ProcessQueryLimitedInformation = 0x1000;
internal const ushort ImageFileMachineUnknown = 0x0000;

[LibraryImport("kernel32.dll", SetLastError = true)]
internal static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool CloseHandle(IntPtr handle);

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static partial bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
```

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "HookHostPathsTests|HookProcessBitnessDetectorTests" -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-paths\
```

Expected: path and bitness tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/ListaryOpen.Infrastructure/Hooks/HookHostPaths.cs src/ListaryOpen.Infrastructure/Hooks/HookProcessBitnessDetector.cs src/ListaryOpen.Infrastructure/Windows/NativeMethods.cs tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostPathsTests.cs tests/ListaryOpen.Infrastructure.Tests/Hooks/HookProcessBitnessDetectorTests.cs
git commit -m "feat: resolve hook host paths and process bitness"
```

## Task 3: IPC Message Contracts

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookIpcMessages.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcMessagesTests.cs`

- [ ] **Step 1: Write failing serialization tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcMessagesTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run failing tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter HookIpcMessagesTests -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-ipc-contracts\
```

Expected: build fails because IPC message classes do not exist.

- [ ] **Step 3: Implement JSON IPC messages**

Create `src/ListaryOpen.Infrastructure/Hooks/HookIpcMessages.cs`:

```csharp
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
```

- [ ] **Step 4: Run IPC message tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter HookIpcMessagesTests -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-ipc-contracts\
```

Expected: IPC message tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/ListaryOpen.Infrastructure/Hooks/HookIpcMessages.cs tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcMessagesTests.cs
git commit -m "feat: add hook ipc message contracts"
```

## Task 4: Hook Bridge Routing And Fallback Boundary

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`
- Modify: `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeHookTests.cs`

- [ ] **Step 1: Write failing bridge tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookQuickSwitchBridgeTests
{
    [Fact]
    public async Task JumpUsesActiveDialogArchitectureClient()
    {
        var dialog = new HookDialogContext(
            "dlg-1",
            new IntPtr(100),
            200,
            300,
            HookArchitecture.X86,
            "MobaXterm",
            "#32770",
            "Choose which file(s) to upload...",
            DateTimeOffset.UtcNow);
        var client = new RecordingHookClient(dialog, HookJumpResult.Success("Jumped."));
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = client
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.Success, result.Status);
        Assert.Equal("dlg-1", client.LastDialogId);
        Assert.Equal("C:\\Users\\paulx", client.LastFolderPath);
    }

    [Fact]
    public async Task JumpReturnsNoActiveDialogWhenNoClientHasDialog()
    {
        var bridge = new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>
            {
                [HookArchitecture.X64] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog.")),
                [HookArchitecture.X86] = new RecordingHookClient(null, new HookJumpResult(HookJumpStatus.NoActiveDialog, "No dialog."))
            });

        var result = await bridge.JumpActiveDialogToFolderAsync("C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.NoActiveDialog, result.Status);
    }

    private sealed class RecordingHookClient : IHookIpcClient
    {
        private readonly HookDialogContext? _activeDialog;
        private readonly HookJumpResult _jumpResult;

        public RecordingHookClient(HookDialogContext? activeDialog, HookJumpResult jumpResult)
        {
            _activeDialog = activeDialog;
            _jumpResult = jumpResult;
        }

        public string? LastDialogId { get; private set; }

        public string? LastFolderPath { get; private set; }

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_activeDialog);

        public Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
        {
            LastDialogId = dialogId;
            LastFolderPath = folderPath;
            return Task.FromResult(_jumpResult);
        }
    }
}
```

Create `tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeHookTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Dialog;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogBridgeHookTests
{
    [Fact]
    public async Task JumpToFolderUsesHookBeforeFallbackAutomation()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-jump-");
        var hook = new FakeHookBridge(HookJumpResult.Success("Hook changed folder."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.Success, result.Status);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(0, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JumpToFolderFallsBackWhenHookHasNoActiveDialog()
    {
        var folder = Directory.CreateTempSubdirectory("listary-open-hook-fallback-");
        var hook = new FakeHookBridge(new HookJumpResult(HookJumpStatus.NoActiveDialog, "No active hook dialog."));
        var fallback = new FakeDialogAutomation(DialogProbeResult.StandardDialog());
        var bridge = new DialogBridge(fallback, hook);

        try
        {
            var result = await bridge.JumpToFolderAsync(folder.FullName, CancellationToken.None);

            Assert.Equal(DialogJumpStatus.Success, result.Status);
            Assert.Equal(1, hook.JumpCount);
            Assert.Equal(1, fallback.ProbeCallCount);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private sealed class FakeHookBridge : IHookQuickSwitchBridge
    {
        private readonly HookJumpResult _jumpResult;

        public FakeHookBridge(HookJumpResult jumpResult)
        {
            _jumpResult = jumpResult;
        }

        public HookQuickSwitchStatus Status => HookQuickSwitchStatus.Disabled();

        public int JumpCount { get; private set; }

        public event EventHandler<HookQuickSwitchStatus>? StatusChanged;

        public Task EnableAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) => Task.FromResult<HookDialogContext?>(null);

        public Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
        {
            JumpCount++;
            return Task.FromResult(_jumpResult);
        }

        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 2: Run failing bridge tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "HookQuickSwitchBridgeTests|DialogBridgeHookTests" -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-bridge\
```

Expected: build fails because `HookQuickSwitchBridge`, `IHookIpcClient`, and `DialogBridge` overload do not exist.

- [ ] **Step 3: Implement hook client interface and bridge**

Create `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs` with the interface first:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public interface IHookIpcClient
{
    Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken);

    Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken);
}
```

Create `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookQuickSwitchBridge : IHookQuickSwitchBridge
{
    private readonly IReadOnlyDictionary<HookArchitecture, IHookIpcClient> _clients;

    public HookQuickSwitchBridge(
        HookQuickSwitchStatus initialStatus,
        IReadOnlyDictionary<HookArchitecture, IHookIpcClient> clients)
    {
        Status = initialStatus;
        _clients = clients;
    }

    public HookQuickSwitchStatus Status { get; private set; }

    public event EventHandler<HookQuickSwitchStatus>? StatusChanged;

    public Task EnableAsync(CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public async Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _clients.Values)
        {
            var dialog = await client.GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
            if (dialog is not null)
            {
                return dialog;
            }
        }

        return null;
    }

    public async Task<HookJumpResult> JumpActiveDialogToFolderAsync(string folderPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path cannot be empty.", nameof(folderPath));
        }

        var dialog = await GetActiveDialogAsync(cancellationToken).ConfigureAwait(false);
        if (dialog is null)
        {
            return new HookJumpResult(HookJumpStatus.NoActiveDialog, "No active hook-controlled dialog.");
        }

        if (!_clients.TryGetValue(dialog.Architecture, out var client))
        {
            return new HookJumpResult(HookJumpStatus.HostUnavailable, $"{dialog.Architecture} hook host is not available.");
        }

        return await client.JumpDialogToFolderAsync(dialog.DialogId, folderPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values.OfType<IDisposable>())
        {
            client.Dispose();
        }
    }
}
```

- [ ] **Step 4: Modify DialogBridge to prefer hook**

Modify constructor and `JumpToFolderAsync` in `src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs`:

```csharp
private readonly IHookQuickSwitchBridge? _hookBridge;

public DialogBridge(IDialogAutomation automation, IHookQuickSwitchBridge? hookBridge = null)
{
    _automation = automation ?? throw new ArgumentNullException(nameof(automation));
    _hookBridge = hookBridge;
}
```

Add this block after folder path validation and before `_automation.ProbeActiveDialog()`:

```csharp
if (_hookBridge is not null && Directory.Exists(folderPath))
{
    var hookResult = await _hookBridge.JumpActiveDialogToFolderAsync(folderPath, cancellationToken).ConfigureAwait(false);
    if (hookResult.Status == HookJumpStatus.Success)
    {
        return new DialogJumpResult(DialogJumpStatus.Success, hookResult.Message);
    }

    if (hookResult.Status is HookJumpStatus.AccessDenied)
    {
        return new DialogJumpResult(DialogJumpStatus.PermissionLimited, hookResult.Message);
    }

    if (hookResult.Status is HookJumpStatus.TargetGone)
    {
        return new DialogJumpResult(DialogJumpStatus.TargetGone, hookResult.Message);
    }
}
```

Add `using ListaryOpen.Infrastructure.Hooks;`.

- [ ] **Step 5: Run bridge tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "HookQuickSwitchBridgeTests|DialogBridgeHookTests" -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-bridge\
```

Expected: bridge routing tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs src/ListaryOpen.Infrastructure/Dialog/DialogBridge.cs tests/ListaryOpen.Infrastructure.Tests/Hooks/HookQuickSwitchBridgeTests.cs tests/ListaryOpen.Infrastructure.Tests/Dialog/DialogBridgeHookTests.cs
git commit -m "feat: route dialog jumps through hook bridge first"
```

## Task 5: Settings Enable Command And App Wiring

**Files:**
- Modify: `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/ListaryOpen.App/MainWindow.xaml`
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs`

- [ ] **Step 1: Write failing settings tests**

Create `tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs`:

```csharp
using ListaryOpen.App.ViewModels;
using ListaryOpen.Core.Settings;
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class SettingsHookQuickSwitchTests
{
    [Fact]
    public void ConstructorStartsWithHookDisabledStatus()
    {
        var viewModel = new SettingsViewModel(AppSettings.Defaults());

        Assert.Equal("Hook quick switch: disabled", viewModel.HookQuickSwitchStatusText);
        Assert.True(viewModel.EnableHookQuickSwitchCommand.CanExecute(null));
    }

    [Fact]
    public void EnableHookCommandInvokesCallbackAndUpdatesStatus()
    {
        var enableCount = 0;
        var viewModel = new SettingsViewModel(
            AppSettings.Defaults(),
            enableNtfsFastIndexing: null,
            enableHookQuickSwitch: () => enableCount++);

        viewModel.UpdateHookQuickSwitchStatus(new HookQuickSwitchStatus(
            true,
            new HookArchitectureStatus(HookArchitecture.X64, true, true, true, "x64 running"),
            new HookArchitectureStatus(HookArchitecture.X86, true, false, true, "x86 stopped")));
        viewModel.EnableHookQuickSwitchCommand.Execute(null);

        Assert.Equal(1, enableCount);
        Assert.Contains("partial", viewModel.HookQuickSwitchStatusText);
    }
}
```

- [ ] **Step 2: Run failing settings test**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter SettingsHookQuickSwitchTests -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-settings\
```

Expected: build fails because `SettingsViewModel` does not expose hook properties.

- [ ] **Step 3: Update SettingsViewModel**

Modify `src/ListaryOpen.App/ViewModels/SettingsViewModel.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;
```

Add fields:

```csharp
private readonly Action _enableHookQuickSwitch;
private HookQuickSwitchStatus _hookQuickSwitchStatus = HookQuickSwitchStatus.Disabled();
```

Replace constructors with overloads that preserve old call sites:

```csharp
public SettingsViewModel(AppSettings settings, Action? enableNtfsFastIndexing)
    : this(settings, enableNtfsFastIndexing, null)
{
}

public SettingsViewModel(AppSettings settings, Action? enableNtfsFastIndexing, Action? enableHookQuickSwitch)
{
    Settings = settings;
    _enableNtfsFastIndexing = enableNtfsFastIndexing ?? (() => { });
    _enableHookQuickSwitch = enableHookQuickSwitch ?? (() => { });
    EnableNtfsFastIndexingCommand = new RelayCommand(
        EnableNtfsFastIndexing,
        () => !NtfsFastIndexingEnabled);
    EnableHookQuickSwitchCommand = new RelayCommand(
        EnableHookQuickSwitch,
        () => true);
}
```

Add properties and methods:

```csharp
public ICommand EnableHookQuickSwitchCommand { get; }

public string HookQuickSwitchStatusText => _hookQuickSwitchStatus.DisplayText;

public void UpdateHookQuickSwitchStatus(HookQuickSwitchStatus status)
{
    ArgumentNullException.ThrowIfNull(status);

    if (Equals(_hookQuickSwitchStatus, status))
    {
        return;
    }

    _hookQuickSwitchStatus = status;
    OnPropertyChanged(nameof(HookQuickSwitchStatusText));
}

private void EnableHookQuickSwitch()
{
    _enableHookQuickSwitch();
}
```

- [ ] **Step 4: Update Settings XAML**

Modify `src/ListaryOpen.App/MainWindow.xaml` by adding a second status/action grid below the NTFS grid:

```xml
<Grid Margin="0,16,0,0">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>

    <StackPanel Grid.Column="0"
                Margin="0,0,12,0">
        <TextBlock Text="Hook quick switch"
                   FontSize="14"
                   FontWeight="SemiBold"
                   Margin="0,0,0,6" />
        <TextBlock Text="{Binding HookQuickSwitchStatusText}"
                   TextWrapping="Wrap" />
    </StackPanel>

    <Button Grid.Column="1"
            MinWidth="80"
            Padding="10,4"
            VerticalAlignment="Center"
            Content="Enable"
            Command="{Binding EnableHookQuickSwitchCommand}" />
</Grid>
```

- [ ] **Step 5: Wire bridge lifecycle in App**

Modify `src/ListaryOpen.App/App.xaml.cs`:

```csharp
private IHookQuickSwitchBridge? _hookQuickSwitchBridge;
```

In service initialization after `_dialogAutomation = new WindowsDialogAutomation();`:

```csharp
_hookQuickSwitchBridge = HookQuickSwitchBridgeFactory.CreateDefault();
_hookQuickSwitchBridge.StatusChanged += OnHookQuickSwitchStatusChanged;
_dialogBridge = new DialogBridge(_dialogAutomation, _hookQuickSwitchBridge);
```

Replace settings view model construction:

```csharp
_settingsViewModel = new SettingsViewModel(
    AppSettings.Defaults(),
    EnableNtfsFastIndexing,
    EnableHookQuickSwitch);
_settingsViewModel.UpdateHookQuickSwitchStatus(_hookQuickSwitchBridge.Status);
```

Add methods:

```csharp
private void EnableHookQuickSwitch()
{
    var bridge = _hookQuickSwitchBridge;
    if (bridge is null)
    {
        return;
    }

    _ = EnableHookQuickSwitchAsync(bridge);
}

private async Task EnableHookQuickSwitchAsync(IHookQuickSwitchBridge bridge)
{
    try
    {
        await bridge.EnableAsync(_shutdownCancellation.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        Trace.TraceError(exception.ToString());
    }
}

private void OnHookQuickSwitchStatusChanged(object? sender, HookQuickSwitchStatus status)
{
    if (IsShuttingDown)
    {
        return;
    }

    _ = InvokeOnDispatcherAsync(Dispatcher, () => _settingsViewModel?.UpdateHookQuickSwitchStatus(status));
}
```

In `OnExit`, unsubscribe and dispose:

```csharp
if (_hookQuickSwitchBridge is not null)
{
    _hookQuickSwitchBridge.StatusChanged -= OnHookQuickSwitchStatusChanged;
    _hookQuickSwitchBridge.Dispose();
}
```

- [ ] **Step 6: Add a bridge factory skeleton**

Create `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Hooks;

public static class HookQuickSwitchBridgeFactory
{
    public static IHookQuickSwitchBridge CreateDefault()
    {
        return new HookQuickSwitchBridge(
            HookQuickSwitchStatus.Disabled(),
            new Dictionary<HookArchitecture, IHookIpcClient>());
    }
}
```

- [ ] **Step 7: Run settings and app tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter "SettingsHookQuickSwitchTests|SettingsViewModelTests|AppDialogHotkeyTests" -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-settings\
```

Expected: selected tests pass.

- [ ] **Step 8: Commit**

```powershell
git add src/ListaryOpen.App/App.xaml.cs src/ListaryOpen.App/MainWindow.xaml src/ListaryOpen.App/ViewModels/SettingsViewModel.cs src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs tests/ListaryOpen.Infrastructure.Tests/App/SettingsHookQuickSwitchTests.cs tests/ListaryOpen.Infrastructure.Tests/App/AppDialogHotkeyTests.cs
git commit -m "feat: expose hook quick switch enablement"
```

## Task 6: Native Hook Workspace And Toolchain Gate

**Files:**
- Create: `native/ListaryOpen.Hooks/Cargo.toml`
- Create: `native/ListaryOpen.Hooks/.cargo/config.toml`
- Create: `native/ListaryOpen.Hooks/hook_common/Cargo.toml`
- Create: `native/ListaryOpen.Hooks/hook_common/src/lib.rs`
- Create: `native/ListaryOpen.Hooks/hook_host/Cargo.toml`
- Create: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Create: `native/ListaryOpen.Hooks/hook_dll/Cargo.toml`
- Create: `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`

- [ ] **Step 1: Verify Rust targets**

Run:

```powershell
rustup target list --installed
rustup target add i686-pc-windows-msvc
```

Expected: installed targets include `x86_64-pc-windows-msvc` and `i686-pc-windows-msvc`.

- [ ] **Step 2: Verify MSVC linker availability**

Run:

```powershell
where link
```

Expected: `link.exe` resolves to a Visual Studio Build Tools path. If it does not, install Visual Studio Build Tools with the C++ workload, open a new Developer PowerShell, and rerun `where link`.

If `link.exe` is not available in this environment and Visual Studio Build Tools cannot be installed non-interactively, use Rust's LLVM Windows targets as the native build fallback:

```powershell
rustup target add x86_64-pc-windows-gnullvm i686-pc-windows-gnullvm
```

The fallback still must produce both 64-bit and 32-bit Windows host/DLL binaries. Record the MSVC linker absence in the task report and use the fallback targets consistently in later packaging steps unless MSVC becomes available.

- [ ] **Step 3: Create Rust workspace manifests**

Create `native/ListaryOpen.Hooks/Cargo.toml`:

```toml
[workspace]
resolver = "2"
members = [
  "hook_common",
  "hook_host",
  "hook_dll"
]
```

Create `native/ListaryOpen.Hooks/.cargo/config.toml`:

```toml
[build]
target-dir = "target"
```

Create `native/ListaryOpen.Hooks/hook_common/Cargo.toml`:

```toml
[package]
name = "listary_open_hook_common"
version = "0.1.0"
edition = "2021"

[dependencies]
```

Create `native/ListaryOpen.Hooks/hook_host/Cargo.toml`:

```toml
[package]
name = "listary_open_hook_host"
version = "0.1.0"
edition = "2021"

[dependencies]
listary_open_hook_common = { path = "../hook_common" }
serde = { version = "1", features = ["derive"] }
serde_json = "1"
windows-sys = { version = "0.59", features = [
  "Win32_Foundation",
  "Win32_System_Threading",
  "Win32_UI_WindowsAndMessaging"
] }
```

Create `native/ListaryOpen.Hooks/hook_dll/Cargo.toml`:

```toml
[package]
name = "listary_open_hook"
version = "0.1.0"
edition = "2021"

[lib]
crate-type = ["cdylib"]

[dependencies]
listary_open_hook_common = { path = "../hook_common" }
windows-sys = { version = "0.59", features = [
  "Win32_Foundation",
  "Win32_UI_WindowsAndMessaging"
] }
```

- [ ] **Step 4: Add minimal native source**

Create `native/ListaryOpen.Hooks/hook_common/src/lib.rs`:

```rust
pub const HOOK_MAGIC: &str = "ListaryOpenHookV1";
pub const HOOK_DLL_EXPORT: &[u8] = b"ListaryOpenHookProc\0";
```

Create `native/ListaryOpen.Hooks/hook_host/src/main.rs`:

```rust
fn main() {
    println!("ListaryOpen hook host started.");
}
```

Create `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`:

```rust
use windows_sys::Win32::Foundation::{LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::CallNextHookEx;

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenHookProc(code: i32, w_param: WPARAM, l_param: LPARAM) -> LRESULT {
    CallNextHookEx(std::ptr::null_mut(), code, w_param, l_param)
}
```

- [ ] **Step 5: Build both architectures**

Run:

```powershell
Push-Location native\ListaryOpen.Hooks
cargo build -p listary_open_hook_host --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook_host --target i686-pc-windows-msvc
cargo build -p listary_open_hook --target i686-pc-windows-msvc
Pop-Location
```

Expected: four native binaries are produced under `native\ListaryOpen.Hooks\target\{target}\debug\`.

- [ ] **Step 6: Commit**

```powershell
git add native/ListaryOpen.Hooks
git commit -m "feat: add native hook workspace"
```

## Task 7: Native Host Health Pipe

**Files:**
- Modify: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcClientTests.cs`

- [ ] **Step 1: Write failing managed health client test**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcClientTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookIpcClientTests
{
    [Fact]
    public async Task MissingPipeReturnsHostUnavailable()
    {
        var client = new HookIpcClient(@"listary-open-missing-" + Guid.NewGuid(), TimeSpan.FromMilliseconds(50));

        var result = await client.JumpDialogToFolderAsync("dlg", "C:\\Users\\paulx", CancellationToken.None);

        Assert.Equal(HookJumpStatus.HostUnavailable, result.Status);
    }
}
```

- [ ] **Step 2: Implement named pipe client timeout behavior**

Extend `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`:

```csharp
using System.IO.Pipes;
using System.Text;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookIpcClient : IHookIpcClient, IDisposable
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;
    private HookDialogContext? _activeDialog;

    public HookIpcClient(string pipeName, TimeSpan connectTimeout)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout;
    }

    public Task<HookDialogContext?> GetActiveDialogAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_activeDialog);

    public async Task<HookJumpResult> JumpDialogToFolderAsync(string dialogId, string folderPath, CancellationToken cancellationToken)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var request = HookIpcSerializer.Serialize(HookIpcEnvelope.Command(
                new HookJumpCommand(dialogId, folderPath, TimeSpan.FromMilliseconds(750))));
            var requestBytes = Encoding.UTF8.GetBytes(request + "\n");
            await pipe.WriteAsync(requestBytes, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return new HookJumpResult(HookJumpStatus.Failed, "Hook host returned an empty response.");
            }

            var envelope = HookIpcSerializer.Deserialize(line);
            if (envelope.Payload is not HookCommandReply reply)
            {
                return new HookJumpResult(HookJumpStatus.Failed, "Hook host returned an unexpected response.");
            }

            return new HookJumpResult(ParseStatus(reply.Status), reply.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HookJumpResult(HookJumpStatus.Timeout, "Hook host did not respond before timeout.");
        }
        catch (IOException exception)
        {
            return new HookJumpResult(HookJumpStatus.HostUnavailable, exception.Message);
        }
    }

    public void Dispose()
    {
    }

    private static HookJumpStatus ParseStatus(string status) =>
        Enum.TryParse<HookJumpStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : HookJumpStatus.Failed;
}
```

- [ ] **Step 3: Implement host single-command pipe loop**

Modify `native/ListaryOpen.Hooks/hook_host/src/main.rs` so it accepts `--pipe <name>` and replies to `HealthProbe`/`JumpDialogToFolder`:

```rust
use serde::{Deserialize, Serialize};
use std::env;
use std::fs::OpenOptions;
use std::io::{BufRead, BufReader, Write};

#[derive(Deserialize)]
struct Envelope {
    version: u32,
    #[serde(rename = "messageType")]
    message_type: String,
}

#[derive(Serialize)]
struct ReplyEnvelope<'a> {
    version: u32,
    #[serde(rename = "messageType")]
    message_type: &'a str,
    payload: CommandReply<'a>,
}

#[derive(Serialize)]
struct CommandReply<'a> {
    status: &'a str,
    message: &'a str,
}

fn main() -> std::io::Result<()> {
    let pipe_name = env::args()
        .collect::<Vec<_>>()
        .windows(2)
        .find(|pair| pair[0] == "--pipe")
        .map(|pair| pair[1].clone())
        .unwrap_or_else(|| "listary-open-hook-x64".to_string());
    let pipe_path = format!(r"\\.\pipe\{}", pipe_name);

    loop {
        let pipe = OpenOptions::new().read(true).write(true).open(&pipe_path)?;
        let mut reader = BufReader::new(pipe.try_clone()?);
        let mut line = String::new();
        reader.read_line(&mut line)?;
        let envelope: Envelope = serde_json::from_str(&line)?;
        let reply = if envelope.version != 1 {
            ReplyEnvelope {
                version: 1,
                message_type: "CommandReply",
                payload: CommandReply { status: "Failed", message: "Unsupported IPC version." },
            }
        } else if envelope.message_type == "JumpDialogToFolder" {
            ReplyEnvelope {
                version: 1,
                message_type: "CommandReply",
                payload: CommandReply { status: "NoActiveDialog", message: "No active hook dialog." },
            }
        } else {
            ReplyEnvelope {
                version: 1,
                message_type: "CommandReply",
                payload: CommandReply { status: "Success", message: "Hook host healthy." },
            }
        };
        let mut writer = pipe;
        writeln!(writer, "{}", serde_json::to_string(&reply)?)?;
    }
}
```

- [ ] **Step 4: Run managed IPC tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter HookIpcClientTests -p:UseAppHost=false -p:BaseOutputPath=.test-hooks-ipc-client\
```

Expected: `MissingPipeReturnsHostUnavailable` passes.

- [ ] **Step 5: Build native host**

Run:

```powershell
Push-Location native\ListaryOpen.Hooks
cargo build -p listary_open_hook_host --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook_host --target i686-pc-windows-msvc
Pop-Location
```

Expected: both host executables build.

- [ ] **Step 6: Commit**

```powershell
git add native/ListaryOpen.Hooks/hook_host/src/main.rs src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs tests/ListaryOpen.Infrastructure.Tests/Hooks/HookIpcClientTests.cs
git commit -m "feat: add hook host health ipc"
```

## Task 8: Hook Host Startup And Packaging

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Hooks/HookHostProcessFactory.cs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs`
- Modify: `src/ListaryOpen.App/ListaryOpen.App.csproj`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostProcessFactoryTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/ElevatedIndexerProjectTests.cs`

- [ ] **Step 1: Write failing process factory test**

Create `tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostProcessFactoryTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Hooks;

namespace ListaryOpen.Infrastructure.Tests.Hooks;

public sealed class HookHostProcessFactoryTests
{
    [Fact]
    public void CreateStartInfoUsesRunAsWhenElevationRequested()
    {
        var startInfo = HookHostProcessFactory.CreateStartInfo(
            @"C:\App\hooks\x64\ListaryOpen.HookHost.exe",
            "listary-open-hook-x64",
            elevated: true);

        Assert.Equal("runas", startInfo.Verb);
        Assert.True(startInfo.UseShellExecute);
        Assert.Contains("--pipe", startInfo.Arguments);
        Assert.Contains("listary-open-hook-x64", startInfo.Arguments);
    }
}
```

- [ ] **Step 2: Implement process factory**

Create `src/ListaryOpen.Infrastructure/Hooks/HookHostProcessFactory.cs`:

```csharp
using System.Diagnostics;

namespace ListaryOpen.Infrastructure.Hooks;

public sealed class HookHostProcessFactory
{
    public Process? Start(HookArchitecturePaths paths, string pipeName, bool elevated)
    {
        var startInfo = CreateStartInfo(paths.HostExePath, pipeName, elevated);
        return Process.Start(startInfo);
    }

    internal static ProcessStartInfo CreateStartInfo(string hostExePath, string pipeName, bool elevated)
    {
        return new ProcessStartInfo
        {
            FileName = hostExePath,
            Arguments = $"--pipe \"{pipeName}\" --dll \"{Path.Combine(Path.GetDirectoryName(hostExePath) ?? string.Empty, "ListaryOpen.Hook.dll")}\"",
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : string.Empty,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }
}
```

- [ ] **Step 3: Update bridge factory to create clients**

Modify `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs`:

```csharp
public static IHookQuickSwitchBridge CreateDefault()
{
    var paths = HookHostPaths.CreateDefault();
    var clients = new Dictionary<HookArchitecture, IHookIpcClient>
    {
        [HookArchitecture.X64] = new HookIpcClient("listary-open-hook-x64", TimeSpan.FromMilliseconds(500)),
        [HookArchitecture.X86] = new HookIpcClient("listary-open-hook-x86", TimeSpan.FromMilliseconds(500))
    };

    return new HookQuickSwitchBridge(HookQuickSwitchStatus.Disabled(), clients);
}
```

Replace `HookQuickSwitchBridge` constructor with one that accepts host paths and process factory:

```csharp
private readonly HookHostPaths? _paths;
private readonly HookHostProcessFactory? _processFactory;
private readonly List<Process> _hostProcesses = new();

public HookQuickSwitchBridge(
    HookQuickSwitchStatus initialStatus,
    IReadOnlyDictionary<HookArchitecture, IHookIpcClient> clients,
    HookHostPaths? paths = null,
    HookHostProcessFactory? processFactory = null)
{
    Status = initialStatus;
    _clients = clients;
    _paths = paths;
    _processFactory = processFactory;
}
```

Replace `EnableAsync` with:

```csharp
public Task EnableAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();

    if (_paths is null || _processFactory is null)
    {
        Status = new HookQuickSwitchStatus(
            true,
            new HookArchitectureStatus(HookArchitecture.X64, true, false, false, "x64 host factory unavailable"),
            new HookArchitectureStatus(HookArchitecture.X86, true, false, false, "x86 host factory unavailable"));
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    var x64 = StartArchitecture(HookArchitecture.X64);
    var x86 = StartArchitecture(HookArchitecture.X86);
    Status = new HookQuickSwitchStatus(true, x64, x86);
    StatusChanged?.Invoke(this, Status);
    return Task.CompletedTask;
}

private HookArchitectureStatus StartArchitecture(HookArchitecture architecture)
{
    var architecturePaths = _paths!.ForArchitecture(architecture);
    var snapshot = architecturePaths.Snapshot();
    if (!snapshot.HostExists || !snapshot.HookDllExists)
    {
        return new HookArchitectureStatus(
            architecture,
            true,
            false,
            snapshot.HookDllExists,
            $"{architecture.ToFolderName()} hook files missing");
    }

    var pipeName = architecture == HookArchitecture.X64
        ? "listary-open-hook-x64"
        : "listary-open-hook-x86";
    var process = _processFactory!.Start(architecturePaths, pipeName, elevated: true);
    if (process is not null)
    {
        _hostProcesses.Add(process);
        return new HookArchitectureStatus(architecture, true, true, true, $"{architecture.ToFolderName()} host running");
    }

    return new HookArchitectureStatus(architecture, true, false, true, $"{architecture.ToFolderName()} host did not start");
}
```

Update `Dispose` to stop host processes created by the bridge:

```csharp
public void Dispose()
{
    foreach (var client in _clients.Values.OfType<IDisposable>())
    {
        client.Dispose();
    }

    foreach (var process in _hostProcesses)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
```

Update `HookQuickSwitchBridgeFactory.CreateDefault()` to pass `paths` and `HookHostProcessFactory`.

- [ ] **Step 4: Add MSBuild native hook copy target**

Modify `src/ListaryOpen.App/ListaryOpen.App.csproj` with properties:

```xml
<PropertyGroup>
  <NativeHookRoot>$(MSBuildProjectDirectory)\..\..\native\ListaryOpen.Hooks</NativeHookRoot>
  <BuildNativeHooks Condition="'$(BuildNativeHooks)' == ''">true</BuildNativeHooks>
</PropertyGroup>
```

Add target:

```xml
<Target Name="BuildAndCopyNativeHooks" AfterTargets="Build" Condition="'$(BuildNativeHooks)' == 'true'">
  <Exec WorkingDirectory="$(NativeHookRoot)"
        Command="cargo build -p listary_open_hook_host --target x86_64-pc-windows-msvc" />
  <Exec WorkingDirectory="$(NativeHookRoot)"
        Command="cargo build -p listary_open_hook --target x86_64-pc-windows-msvc" />
  <Exec WorkingDirectory="$(NativeHookRoot)"
        Command="cargo build -p listary_open_hook_host --target i686-pc-windows-msvc" />
  <Exec WorkingDirectory="$(NativeHookRoot)"
        Command="cargo build -p listary_open_hook --target i686-pc-windows-msvc" />
  <ItemGroup>
    <HookX64Host Include="$(NativeHookRoot)\target\x86_64-pc-windows-msvc\debug\listary_open_hook_host.exe" />
    <HookX64Dll Include="$(NativeHookRoot)\target\x86_64-pc-windows-msvc\debug\listary_open_hook.dll" />
    <HookX86Host Include="$(NativeHookRoot)\target\i686-pc-windows-msvc\debug\listary_open_hook_host.exe" />
    <HookX86Dll Include="$(NativeHookRoot)\target\i686-pc-windows-msvc\debug\listary_open_hook.dll" />
  </ItemGroup>
  <Copy SourceFiles="@(HookX64Host)" DestinationFiles="$(OutDir)hooks\x64\ListaryOpen.HookHost.exe" SkipUnchangedFiles="true" />
  <Copy SourceFiles="@(HookX64Dll)" DestinationFiles="$(OutDir)hooks\x64\ListaryOpen.Hook.dll" SkipUnchangedFiles="true" />
  <Copy SourceFiles="@(HookX86Host)" DestinationFiles="$(OutDir)hooks\x86\ListaryOpen.HookHost.exe" SkipUnchangedFiles="true" />
  <Copy SourceFiles="@(HookX86Dll)" DestinationFiles="$(OutDir)hooks\x86\ListaryOpen.Hook.dll" SkipUnchangedFiles="true" />
</Target>
```

- [ ] **Step 5: Run tests without native build**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj --no-restore --nologo --filter HookHostProcessFactoryTests -p:UseAppHost=false -p:BuildNativeHooks=false -p:BaseOutputPath=.test-hooks-hostfactory\
```

Expected: process factory tests pass.

- [ ] **Step 6: Run app build with native hooks enabled**

Run:

```powershell
dotnet build src\ListaryOpen.App\ListaryOpen.App.csproj --no-restore --nologo -p:UseAppHost=false -p:BuildNativeHooks=true
```

Expected: app output contains:

```text
src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x64\ListaryOpen.HookHost.exe
src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x64\ListaryOpen.Hook.dll
src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x86\ListaryOpen.HookHost.exe
src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x86\ListaryOpen.Hook.dll
```

- [ ] **Step 7: Commit**

```powershell
git add src/ListaryOpen.Infrastructure/Hooks/HookHostProcessFactory.cs src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridgeFactory.cs src/ListaryOpen.App/ListaryOpen.App.csproj tests/ListaryOpen.Infrastructure.Tests/Hooks/HookHostProcessFactoryTests.cs
git commit -m "feat: package x86 and x64 hook hosts"
```

## Task 9: Native Dialog Discovery Hook

**Files:**
- Modify: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Modify: `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`
- Modify: `native/ListaryOpen.Hooks/hook_common/src/lib.rs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`
- Test: manual native smoke command

- [ ] **Step 1: Extend common constants**

Modify `native/ListaryOpen.Hooks/hook_common/src/lib.rs`:

```rust
pub const HOOK_MAGIC: &str = "ListaryOpenHookV1";
pub const HOOK_DLL_EXPORT: &[u8] = b"ListaryOpenHookProc\0";
pub const DIALOG_CLASS: &str = "#32770";
pub const WM_USER_HOOK_BASE: u32 = 0x0400 + 0x4C4F;
pub const WM_LISTARY_OPEN_JUMP: u32 = WM_USER_HOOK_BASE + 1;
pub const WM_LISTARY_OPEN_REPORT: u32 = WM_USER_HOOK_BASE + 2;
```

- [ ] **Step 2: Implement hook DLL dialog detection**

Modify `native/ListaryOpen.Hooks/hook_dll/src/lib.rs` with:

```rust
use listary_open_hook_common::{DIALOG_CLASS, WM_LISTARY_OPEN_JUMP};
use std::ffi::c_void;
use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetClassNameW, GetWindowTextLengthW, GetWindowTextW, CWPSTRUCT, WM_COPYDATA
};

#[no_mangle]
pub unsafe extern "system" fn ListaryOpenHookProc(code: i32, w_param: WPARAM, l_param: LPARAM) -> LRESULT {
    if code >= 0 {
        let cwp = l_param as *const CWPSTRUCT;
        if !cwp.is_null() {
            let hwnd = (*cwp).hwnd;
            if is_supported_dialog(hwnd) && ((*cwp).message == WM_COPYDATA || (*cwp).message == WM_LISTARY_OPEN_JUMP) {
                handle_dialog_message(hwnd, (*cwp).message, (*cwp).lParam);
            }
        }
    }

    CallNextHookEx(std::ptr::null_mut(), code, w_param, l_param)
}

unsafe fn is_supported_dialog(hwnd: HWND) -> bool {
    let mut class_buffer = [0u16; 64];
    let len = GetClassNameW(hwnd, class_buffer.as_mut_ptr(), class_buffer.len() as i32);
    if len <= 0 {
        return false;
    }

    let class_name = String::from_utf16_lossy(&class_buffer[..len as usize]);
    class_name == DIALOG_CLASS && GetWindowTextLengthW(hwnd) >= 0
}

unsafe fn handle_dialog_message(_hwnd: HWND, _message: u32, _lparam: LPARAM) {
}
```

- [ ] **Step 3: Implement host hook installation**

Modify `native/ListaryOpen.Hooks/hook_host/src/main.rs` to load the DLL path from `--dll`, enumerate top-level `#32770` windows, hook each dialog thread, retain hook handles, and run a message loop. Add these imports:

```rust
use listary_open_hook_common::{DIALOG_CLASS, HOOK_DLL_EXPORT};
use std::collections::HashSet;
use windows_sys::Win32::Foundation::{BOOL, HINSTANCE, HWND, LPARAM, TRUE};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, DispatchMessageW, EnumWindows, GetClassNameW, GetMessageW,
    GetWindowThreadProcessId, SetWindowsHookExW, TranslateMessage, HHOOK, MSG,
    WH_CALLWNDPROC
};
```

Add argument parsing:

```rust
fn arg_value(name: &str) -> Option<String> {
    env::args()
        .collect::<Vec<_>>()
        .windows(2)
        .find(|pair| pair[0] == name)
        .map(|pair| pair[1].clone())
}
```

Add a top-level discovery function:

```rust
unsafe fn discover_dialog_threads() -> HashSet<u32> {
    let mut threads = HashSet::<u32>::new();
    EnumWindows(Some(enum_windows_proc), (&mut threads as *mut HashSet<u32>) as LPARAM);
    threads
}

unsafe extern "system" fn enum_windows_proc(hwnd: HWND, lparam: LPARAM) -> BOOL {
    if is_dialog_window(hwnd) {
        let mut process_id = 0u32;
        let thread_id = GetWindowThreadProcessId(hwnd, &mut process_id);
        if thread_id != 0 {
            let threads = &mut *(lparam as *mut HashSet<u32>);
            threads.insert(thread_id);
        }
    }

    TRUE
}

unsafe fn is_dialog_window(hwnd: HWND) -> bool {
    let mut class_buffer = [0u16; 64];
    let len = GetClassNameW(hwnd, class_buffer.as_mut_ptr(), class_buffer.len() as i32);
    if len <= 0 {
        return false;
    }

    String::from_utf16_lossy(&class_buffer[..len as usize]) == DIALOG_CLASS
}
```

Use this hook install helper:

```rust
unsafe fn install_thread_hook(dll_path: &str, thread_id: u32) -> Result<HHOOK, String> {
    let wide_path: Vec<u16> = dll_path.encode_utf16().chain(std::iter::once(0)).collect();
    let module = LoadLibraryW(wide_path.as_ptr());
    if module == 0 {
        return Err("LoadLibraryW failed for hook DLL.".to_string());
    }

    let proc = GetProcAddress(module, HOOK_DLL_EXPORT.as_ptr());
    if proc.is_null() {
        return Err("GetProcAddress failed for ListaryOpenHookProc.".to_string());
    }

    let hook = SetWindowsHookExW(WH_CALLWNDPROC, Some(std::mem::transmute(proc)), module, thread_id);
    if hook == 0 {
        return Err("SetWindowsHookExW failed.".to_string());
    }

    Ok(hook)
}
```

In `main`, install hooks before entering the pipe loop:

```rust
let dll_path = arg_value("--dll").expect("--dll argument is required");
let mut hooks = Vec::<HHOOK>::new();
unsafe {
    for thread_id in discover_dialog_threads() {
        match install_thread_hook(&dll_path, thread_id) {
            Ok(hook) => hooks.push(hook),
            Err(message) => eprintln!("hook install failed for thread {thread_id}: {message}"),
        }
    }
}
```

Add a message pump on a background thread so hook callbacks can run:

```rust
std::thread::spawn(|| unsafe {
    let mut message = std::mem::zeroed::<MSG>();
    while GetMessageW(&mut message, 0, 0, 0) > 0 {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
});
```

- [ ] **Step 4: Build both hook DLLs and hosts**

Run:

```powershell
Push-Location native\ListaryOpen.Hooks
cargo build -p listary_open_hook_host --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook_host --target i686-pc-windows-msvc
cargo build -p listary_open_hook --target i686-pc-windows-msvc
Pop-Location
```

Expected: both architectures build.

- [ ] **Step 5: Smoke-test hook loading**

Open Notepad++ file open dialog and MobaXterm upload dialog. Start hosts manually from the app output or native target folders:

```powershell
.\src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x64\ListaryOpen.HookHost.exe --pipe listary-open-hook-x64 --dll .\src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x64\ListaryOpen.Hook.dll
.\src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x86\ListaryOpen.HookHost.exe --pipe listary-open-hook-x86 --dll .\src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks\x86\ListaryOpen.Hook.dll
```

Expected: module inspection in Task 12 shows `ListaryOpen.Hook.dll` loaded in the Notepad++ and MobaXterm processes.

- [ ] **Step 6: Commit**

```powershell
git add native/ListaryOpen.Hooks/hook_common/src/lib.rs native/ListaryOpen.Hooks/hook_dll/src/lib.rs native/ListaryOpen.Hooks/hook_host/src/main.rs src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs
git commit -m "feat: install native dialog hooks"
```

## Task 10: Native Jump Command

**Files:**
- Modify: `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`
- Modify: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Modify: `native/ListaryOpen.Hooks/hook_common/src/lib.rs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`

- [ ] **Step 1: Define jump command payload**

Modify `native/ListaryOpen.Hooks/hook_common/src/lib.rs`:

```rust
pub const JUMP_COPYDATA_MAGIC: usize = 0x4C4F_4A55;

#[repr(C)]
pub struct JumpCommandHeader {
    pub magic: usize,
    pub utf16_code_units: u32,
}
```

- [ ] **Step 2: Host sends WM_COPYDATA to dialog**

In `native/ListaryOpen.Hooks/hook_host/src/main.rs`, parse the managed `JumpDialogToFolder` payload and send it to the active dialog:

```rust
unsafe fn send_jump_command(hwnd: HWND, folder_path: &str) -> Result<(), String> {
    let mut utf16: Vec<u16> = folder_path.encode_utf16().collect();
    utf16.push(0);
    let header = JumpCommandHeader {
        magic: JUMP_COPYDATA_MAGIC,
        utf16_code_units: utf16.len() as u32,
    };
    let mut bytes = Vec::<u8>::new();
    bytes.extend_from_slice(std::slice::from_raw_parts(
        (&header as *const JumpCommandHeader) as *const u8,
        std::mem::size_of::<JumpCommandHeader>()));
    bytes.extend_from_slice(std::slice::from_raw_parts(
        utf16.as_ptr() as *const u8,
        utf16.len() * std::mem::size_of::<u16>()));

    let copy = COPYDATASTRUCT {
        dwData: JUMP_COPYDATA_MAGIC,
        cbData: bytes.len() as u32,
        lpData: bytes.as_ptr() as *const c_void,
    };
    let result = SendMessageW(hwnd, WM_COPYDATA, 0, (&copy as *const COPYDATASTRUCT) as LPARAM);
    if result == 0 {
        return Err("Jump command was not handled by the hook.".to_string());
    }

    Ok(())
}
```

- [ ] **Step 3: Hook DLL handles jump without bottom filename field**

In `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`, handle `WM_COPYDATA`:

```rust
unsafe fn handle_dialog_message(hwnd: HWND, message: u32, lparam: LPARAM) -> bool {
    if message != WM_COPYDATA {
        return false;
    }

    let copy = lparam as *const COPYDATASTRUCT;
    if copy.is_null() || (*copy).dwData != JUMP_COPYDATA_MAGIC {
        return false;
    }

    let bytes = std::slice::from_raw_parts((*copy).lpData as *const u8, (*copy).cbData as usize);
    if bytes.len() <= std::mem::size_of::<JumpCommandHeader>() {
        return false;
    }

    let header = &*(bytes.as_ptr() as *const JumpCommandHeader);
    if header.magic != JUMP_COPYDATA_MAGIC {
        return false;
    }

    let text_bytes = &bytes[std::mem::size_of::<JumpCommandHeader>()..];
    let folder = std::slice::from_raw_parts(text_bytes.as_ptr() as *const u16, header.utf16_code_units as usize);
    jump_dialog_to_folder(hwnd, folder)
}
```

Add `jump_dialog_to_folder`:

```rust
unsafe fn jump_dialog_to_folder(hwnd: HWND, folder_utf16: &[u16]) -> bool {
    let address_edit = GetDlgItem(hwnd, 41477);
    if address_edit == 0 {
        return false;
    }

    SendMessageW(address_edit, WM_SETTEXT, 0, folder_utf16.as_ptr() as LPARAM);
    SendMessageW(address_edit, WM_KEYDOWN, VK_RETURN as WPARAM, 0);
    SendMessageW(address_edit, WM_KEYUP, VK_RETURN as WPARAM, 0);
    IsWindow(hwnd) != 0
}
```

This targets the dialog address/navigation control. It does not write to `1152` or other bottom file-name controls.

- [ ] **Step 4: Return handled result from hook proc**

In `ListaryOpenHookProc`, return non-zero for handled `WM_COPYDATA`:

```rust
if handle_dialog_message(hwnd, (*cwp).message, (*cwp).lParam) {
    return 1;
}
```

- [ ] **Step 5: Build native outputs**

Run:

```powershell
Push-Location native\ListaryOpen.Hooks
cargo build -p listary_open_hook_host --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook_host --target i686-pc-windows-msvc
cargo build -p listary_open_hook --target i686-pc-windows-msvc
Pop-Location
```

Expected: both hosts and both DLLs build.

- [ ] **Step 6: Manual jump smoke test**

Open Notepad++ file-open dialog. Open an Explorer window to `C:\Users\paulx`. Enable hook quick switch in Settings. Press `Ctrl+G`.

Expected:

- the Notepad++ dialog changes to `C:\Users\paulx`
- the dialog remains open
- no path is visibly typed character-by-character
- no file is opened

- [ ] **Step 7: Commit**

```powershell
git add native/ListaryOpen.Hooks/hook_common/src/lib.rs native/ListaryOpen.Hooks/hook_dll/src/lib.rs native/ListaryOpen.Hooks/hook_host/src/main.rs src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs
git commit -m "feat: execute folder jumps from hook dll"
```

## Task 11: Target Application Coverage

**Files:**
- Modify: `native/ListaryOpen.Hooks/hook_dll/src/lib.rs`
- Modify: `native/ListaryOpen.Hooks/hook_host/src/main.rs`
- Modify: `src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs`
- Test: `docs/manual-hook-quick-switch-test.md`

- [ ] **Step 1: Add process/title diagnostics to host events**

Add this native dialog record in `native/ListaryOpen.Hooks/hook_host/src/main.rs`:

```rust
#[derive(Clone, Serialize)]
struct ObservedDialog {
    dialog_id: String,
    window_handle: isize,
    thread_id: u32,
    process_id: u32,
    process_name: String,
    class_name: String,
    title: String,
    architecture: String,
}
```

Populate it in the enum window callback by calling `GetWindowThreadProcessId`, `GetClassNameW`, `GetWindowTextW`, and a `process_name(process_id)` helper:

```rust
unsafe fn observed_dialog(hwnd: HWND, architecture: &str) -> Option<ObservedDialog> {
    let class_name = window_class(hwnd);
    if class_name != "#32770" {
        return None;
    }

    let title = window_text(hwnd);
    let mut process_id = 0u32;
    let thread_id = GetWindowThreadProcessId(hwnd, &mut process_id);
    if thread_id == 0 || process_id == 0 {
        return None;
    }

    Some(ObservedDialog {
        dialog_id: format!("{}:{}", process_id, hwnd as isize),
        window_handle: hwnd as isize,
        thread_id,
        process_id,
        process_name: process_name(process_id).unwrap_or_else(|| "unknown".to_string()),
        class_name,
        title,
        architecture: architecture.to_string(),
    })
}
```

In `src/ListaryOpen.Infrastructure/Hooks/HookIpcClient.cs`, map `HookActiveDialogEvent` to managed `HookDialogContext`:

```csharp
internal static HookDialogContext ToContext(HookActiveDialogEvent activeDialog)
{
    var architecture = string.Equals(activeDialog.Architecture, "x86", StringComparison.OrdinalIgnoreCase)
        ? HookArchitecture.X86
        : HookArchitecture.X64;
    return new HookDialogContext(
        activeDialog.DialogId,
        new IntPtr(activeDialog.WindowHandle),
        activeDialog.ProcessId,
        activeDialog.ThreadId,
        architecture,
        activeDialog.ProcessName,
        activeDialog.ClassName,
        activeDialog.Title,
        DateTimeOffset.UtcNow);
}
```

- [ ] **Step 2: Handle Firefox and browser-owned dialogs by shape**

Add this shape predicate in `native/ListaryOpen.Hooks/hook_host/src/main.rs`:

```rust
unsafe fn is_supported_dialog_shape(hwnd: HWND) -> bool {
    if window_class(hwnd) != "#32770" {
        return false;
    }

    let title = window_text(hwnd).to_ascii_lowercase();
    let known_title = title.contains("open")
        || title.contains("upload")
        || title.contains("choose")
        || title.contains("folder");
    let has_address = GetDlgItem(hwnd, 41477) != 0;
    known_title || has_address
}
```

Use `is_supported_dialog_shape(hwnd)` before installing a hook for a discovered dialog thread. Firefox must pass by `#32770` plus supported shape; do not require `process_name == "firefox"`.

- [ ] **Step 3: Handle MobaXterm 32-bit upload dialog**

Add this assertion to manual logging in the x86 host when a dialog is observed:

```rust
if dialog.process_name.eq_ignore_ascii_case("MobaXterm") {
    eprintln!(
        "observed mobaxterm dialog architecture={} class={} title={}",
        dialog.architecture,
        dialog.class_name,
        dialog.title
    );
}
```

The managed bridge must route this dialog to the x86 client by `HookDialogContext.Architecture`.

- [ ] **Step 4: Add manual acceptance checklist**

Create `docs/manual-hook-quick-switch-test.md`:

```markdown
# Hook Quick Switch Manual Acceptance

## Setup

1. Build with native hooks: `dotnet build src\ListaryOpen.App\ListaryOpen.App.csproj --no-restore --nologo -p:BuildNativeHooks=true`.
2. Start `src\ListaryOpen.App\bin\Debug\net8.0-windows\ListaryOpen.App.exe`.
3. Open Explorer at `C:\Users\paulx`.
4. Open Settings from tray and click Hook quick switch Enable.

## Required Targets

- [ ] Antigravity open-folder dialog is detected by x64 hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] Notepad++ open dialog is detected by x64 hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] Chrome upload/open dialog is detected by x64 hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] Firefox upload/open dialog is detected by x64 hook and `Ctrl+G` jumps to `C:\Users\paulx`.
- [ ] MobaXterm upload dialog is detected by x86 hook and `Ctrl+G` jumps to `C:\Users\paulx`.

## Invariants

- [ ] The dialog remains open after every jump.
- [ ] No file is opened, accepted, or uploaded during quick switch.
- [ ] The path is not visibly typed character-by-character.
- [ ] Settings shows x64 and x86 hook health.
- [ ] With hook disabled, Settings reports disabled and quick switch uses fallback only as a visible degradation.
```

- [ ] **Step 5: Commit**

```powershell
git add native/ListaryOpen.Hooks/hook_dll/src/lib.rs native/ListaryOpen.Hooks/hook_host/src/main.rs src/ListaryOpen.Infrastructure/Hooks/HookQuickSwitchBridge.cs docs/manual-hook-quick-switch-test.md
git commit -m "feat: cover target quick switch dialogs"
```

## Task 12: Hook Module Verification Script

**Files:**
- Create: `tools/verify-hook-modules.ps1`

- [ ] **Step 1: Create module verification script**

Create `tools/verify-hook-modules.ps1`:

```powershell
param(
    [string[]]$ProcessNames = @("Antigravity", "notepad++", "chrome", "firefox", "MobaXterm"),
    [string]$ModuleName = "ListaryOpen.Hook.dll"
)

$ErrorActionPreference = "Stop"
$results = foreach ($name in $ProcessNames) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        $process = $_
        $loaded = $false
        $modulePath = $null
        try {
            foreach ($module in $process.Modules) {
                if ([string]::Equals($module.ModuleName, $ModuleName, [StringComparison]::OrdinalIgnoreCase)) {
                    $loaded = $true
                    $modulePath = $module.FileName
                    break
                }
            }
        }
        catch {
            $modulePath = "module enumeration failed: $($_.Exception.Message)"
        }

        [pscustomobject]@{
            ProcessName = $process.ProcessName
            Id = $process.Id
            HookLoaded = $loaded
            HookModulePath = $modulePath
        }
    }
}

$results | Format-Table -AutoSize
if ($results.Count -eq 0) {
    throw "No target processes found."
}

$missing = $results | Where-Object { -not $_.HookLoaded }
if ($missing) {
    throw "Hook module missing from one or more target processes."
}
```

- [ ] **Step 2: Run module verification with dialogs open**

Run:

```powershell
.\tools\verify-hook-modules.ps1
```

Expected: every currently open target process with a supported dialog reports `HookLoaded = True`.

- [ ] **Step 3: Commit**

```powershell
git add tools/verify-hook-modules.ps1
git commit -m "test: add hook module verification script"
```

## Task 13: Full Verification And Review

**Files:**
- All files changed by this plan.

- [ ] **Step 1: Run managed test suite**

Run:

```powershell
dotnet test ListaryOpen.sln --no-restore --nologo -m:1 -p:UseAppHost=false -p:BuildNativeHooks=false -p:BaseOutputPath=.test-all\
```

Expected: Core and Infrastructure tests pass.

- [ ] **Step 2: Run full app build with native hooks**

Run:

```powershell
dotnet build ListaryOpen.sln --no-restore --nologo -m:1 -p:BuildNativeHooks=true
```

Expected: build succeeds and app output contains both x64 and x86 hook host/DLL pairs.

- [ ] **Step 3: Run native builds directly**

Run:

```powershell
Push-Location native\ListaryOpen.Hooks
cargo test
cargo build -p listary_open_hook_host --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook --target x86_64-pc-windows-msvc
cargo build -p listary_open_hook_host --target i686-pc-windows-msvc
cargo build -p listary_open_hook --target i686-pc-windows-msvc
Pop-Location
```

Expected: Rust tests and both architecture builds pass.

- [ ] **Step 4: Manual target acceptance**

Run the checklist in `docs/manual-hook-quick-switch-test.md`.

Expected: Antigravity, Notepad++, Chrome, Firefox, and MobaXterm all hook-detect and quick-switch successfully, with dialogs staying open.

- [ ] **Step 5: Dispatch subagent code reviews**

Use fresh subagents with these review scopes:

```text
Review 1: managed bridge, settings, app routing, fallback boundary, and tests.
Review 2: native hook host/DLL Win32 correctness, x86/x64 assumptions, message handling, and dialog safety.
Review 3: build packaging, UAC behavior, verification scripts, and manual acceptance coverage.
```

Require each review to list blocking findings first with file/line references. Fix every blocking finding and rerun the relevant tests.

- [ ] **Step 6: Final completion audit**

Verify each requirement from `docs/superpowers/specs/2026-07-05-hook-quick-switch-design.md` against current evidence:

```text
x64 hook loads into Antigravity, Notepad++, Chrome, Firefox
x86 hook loads into MobaXterm
Ctrl+G quick-switches each target dialog through hook path
dialogs remain open
no UIA strategy exists
fallback exists only for hook unavailable/failure
settings shows per-architecture hook health
app build copies hooks under program directory
managed tests pass
native builds pass
subagent reviews completed and blocking findings fixed
```

- [ ] **Step 7: Commit final fixes**

```powershell
git status --short
git add <only files changed for final fixes>
git commit -m "fix: complete hook quick switch verification"
```

Do not stage unrelated worktree changes.
