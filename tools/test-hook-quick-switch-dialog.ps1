[CmdletBinding()]
param(
    [string[]]$ProcessNames = @(),
    [string]$TargetFolder = $env:USERPROFILE,
    [string]$InfrastructureDllPath = "",
    [int]$ConnectTimeoutMs = 1000,
    [switch]$NoJump
)

$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "This script requires PowerShell 7+ (`pwsh`) because it loads .NET 8 assemblies."
}

if ([string]::IsNullOrWhiteSpace($InfrastructureDllPath)) {
    $repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
    $InfrastructureDllPath = Join-Path $repositoryRoot "src\ListaryOpen.Infrastructure\bin\Debug\net8.0-windows\ListaryOpen.Infrastructure.dll"
}

if (-not (Test-Path -LiteralPath $InfrastructureDllPath)) {
    throw "ListaryOpen.Infrastructure.dll was not found at '$InfrastructureDllPath'. Build the app first."
}

Add-Type -Path $InfrastructureDllPath

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public delegate bool EnumWindowsProcHookQuickSwitchDialogProbe(IntPtr hWnd, IntPtr lParam);

public static class HookQuickSwitchDialogProbeWin32 {
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProcHookQuickSwitchDialogProbe lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
"@

function Test-TargetProcess {
    param(
        [System.Diagnostics.Process]$Process,
        [string[]]$TargetNames
    )

    if ($TargetNames.Count -eq 0) {
        return $true
    }

    foreach ($targetName in $TargetNames) {
        $baseName = [IO.Path]::GetFileNameWithoutExtension($targetName)
        if ($Process.ProcessName -ieq $baseName -or
            $Process.ProcessName -like "*$baseName*" -or
            $Process.MainWindowTitle -like "*$targetName*") {
            return $true
        }
    }

    return $false
}

function Get-HookProbeWindowKind {
    param(
        [string]$ProcessName,
        [string]$ClassName,
        [string]$Title
    )

    if ($ClassName -eq "#32770") {
        return "StandardDialog"
    }

    if ($ProcessName -ieq "blender" -and
        $ClassName -eq "GHOST_WindowClass" -and
        $Title.Trim() -ieq "Blender File View") {
        return "CustomFileBrowser"
    }

    return "CustomWindow"
}

$processes = @(Get-Process | Where-Object { Test-TargetProcess -Process $_ -TargetNames $ProcessNames })
if ($processes.Count -eq 0) {
    [pscustomobject]@{
        Stage = "Process"
        Status = "Missing"
        Message = "No matching process was found."
    }
    exit 2
}

$windows = New-Object System.Collections.Generic.List[object]
foreach ($process in $processes) {
    $processId = [uint32]$process.Id
    $callback = [EnumWindowsProcHookQuickSwitchDialogProbe] {
        param([IntPtr]$hWnd, [IntPtr]$lParam)

        if (-not [HookQuickSwitchDialogProbeWin32]::IsWindowVisible($hWnd)) {
            return $true
        }

        $windowProcessId = 0
        $threadId = [HookQuickSwitchDialogProbeWin32]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId)
        if ($windowProcessId -ne $processId) {
            return $true
        }

        $className = [Text.StringBuilder]::new(256)
        $title = [Text.StringBuilder]::new(512)
        [void][HookQuickSwitchDialogProbeWin32]::GetClassName($hWnd, $className, $className.Capacity)
        [void][HookQuickSwitchDialogProbeWin32]::GetWindowText($hWnd, $title, $title.Capacity)
        $windows.Add([pscustomobject]@{
            Stage = "Window"
            HwndValue = $hWnd
            Hwnd = ("0x{0:X}" -f $hWnd.ToInt64())
            Process = $process.ProcessName
            ProcessId = $windowProcessId
            ThreadId = $threadId
            ClassName = $className.ToString()
            Title = $title.ToString()
            Kind = Get-HookProbeWindowKind -ProcessName $process.ProcessName -ClassName $className.ToString() -Title $title.ToString()
        })

        return $true
    }

    [void][HookQuickSwitchDialogProbeWin32]::EnumWindows($callback, [IntPtr]::Zero)
}

function Test-PreferredDialogTitle {
    param([string]$Title)

    $value = $Title.ToLowerInvariant()
    return $value.Contains("open") -or
        $value.Contains("upload") -or
        $value.Contains("choose") -or
        $value.Contains("folder")
}

$dialog = $windows |
    Where-Object { $_.ClassName -eq "#32770" -and (Test-PreferredDialogTitle -Title $_.Title) } |
    Select-Object -First 1
if ($null -eq $dialog) {
    $dialog = $windows | Where-Object { $_.ClassName -eq "#32770" } | Select-Object -First 1
}
if ($null -ne $dialog) {
    [void][HookQuickSwitchDialogProbeWin32]::SetForegroundWindow($dialog.HwndValue)
    Start-Sleep -Milliseconds 250
}

$windows |
    Select-Object Stage,Hwnd,Process,ProcessId,ThreadId,ClassName,Title,Kind

$timeout = [TimeSpan]::FromMilliseconds($ConnectTimeoutMs)
$pipes = @(
    @{ Architecture = "x64"; PipeName = "listary-open-hook-x64" },
    @{ Architecture = "x86"; PipeName = "listary-open-hook-x86" }
)

foreach ($pipe in $pipes) {
    $client = [ListaryOpen.Infrastructure.Hooks.HookIpcClient]::new($pipe.PipeName, $timeout)
    $active = $client.GetActiveDialogResultAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $activeDialog = $active.Dialog
    if ($null -eq $activeDialog) {
        [pscustomobject]@{
            Stage = "Hook"
            Architecture = $pipe.Architecture
            ActiveStatus = $active.Status
            ActiveMessage = $active.Message
            Dialog = $null
            JumpStatus = $null
            JumpMessage = $null
        }
        continue
    }

    $jumpStatus = "Skipped"
    $jumpMessage = "Jump skipped by -NoJump."
    if (-not $NoJump) {
        $jump = $client.JumpDialogToFolderAsync(
            $activeDialog.DialogId,
            $TargetFolder,
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $jumpStatus = $jump.Status
        $jumpMessage = $jump.Message
    }

    [pscustomobject]@{
        Stage = "Hook"
        Architecture = $pipe.Architecture
        ActiveStatus = $active.Status
        ActiveMessage = $active.Message
        Dialog = "$($activeDialog.ProcessName)/$($activeDialog.ClassName)/$($activeDialog.Title) pid=$($activeDialog.ProcessId) tid=$($activeDialog.ThreadId) id=$($activeDialog.DialogId)"
        JumpStatus = $jumpStatus
        JumpMessage = $jumpMessage
    }
}
