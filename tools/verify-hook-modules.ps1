param(
    [string[]]$ProcessNames = @("Antigravity", "notepad++", "chrome", "firefox", "MobaXterm"),
    [string]$ModuleName = "ListaryOpen.Hook.dll",
    [switch]$AllMatchingProcesses,
    [switch]$SkipDialogPing
)

$ErrorActionPreference = "Stop"

function Add-DialogNativeMethods {
    if ("ListaryOpenVerifyHookNativeMethods" -as [type]) {
        return
    }

    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class ListaryOpenVerifyHookNativeMethods
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
"@
}

function Test-TargetProcessName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProcessName,
        [Parameter(Mandatory = $true)]
        [string[]]$TargetNames
    )

    foreach ($targetName in $TargetNames) {
        if ([string]::IsNullOrWhiteSpace($targetName)) {
            continue
        }

        if ([string]::Equals($ProcessName, $targetName, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }

        if ($ProcessName.StartsWith($targetName + " ", [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-AllMatchingTargetProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$TargetNames
    )

    foreach ($targetName in $TargetNames) {
        Get-Process -Name $targetName -ErrorAction SilentlyContinue
    }
}

function Get-DialogOwnerTargetProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$TargetNames
    )

    Add-DialogNativeMethods

    $targetsByProcessId = @{}

    $callback = [ListaryOpenVerifyHookNativeMethods+EnumWindowsProc]{
        param([IntPtr]$windowHandle, [IntPtr]$lParam)

        if (-not [ListaryOpenVerifyHookNativeMethods]::IsWindowVisible($windowHandle)) {
            return $true
        }

        $className = New-Object System.Text.StringBuilder 256
        [void][ListaryOpenVerifyHookNativeMethods]::GetClassName($windowHandle, $className, $className.Capacity)
        if ($className.ToString() -ne "#32770") {
            return $true
        }

        [uint32]$ownerProcessId = 0
        [void][ListaryOpenVerifyHookNativeMethods]::GetWindowThreadProcessId($windowHandle, [ref]$ownerProcessId)
        if ($ownerProcessId -eq 0) {
            return $true
        }

        try {
            $process = Get-Process -Id ([int]$ownerProcessId) -ErrorAction Stop
        }
        catch {
            return $true
        }

        if (-not (Test-TargetProcessName -ProcessName $process.ProcessName -TargetNames $TargetNames)) {
            return $true
        }

        $processId = [int]$ownerProcessId
        if (-not $targetsByProcessId.ContainsKey($processId)) {
            $targetsByProcessId[$processId] = [pscustomobject]@{
                Process = $process
                DialogHandles = New-Object System.Collections.ArrayList
            }
        }

        [void]$targetsByProcessId[$processId].DialogHandles.Add($windowHandle)
        return $true
    }

    [void][ListaryOpenVerifyHookNativeMethods]::EnumWindows($callback, [IntPtr]::Zero)

    foreach ($target in $targetsByProcessId.Values) {
        $target
    }
}

function Invoke-DialogPing {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IEnumerable]$DialogHandles
    )

    Add-DialogNativeMethods

    foreach ($dialogHandle in $DialogHandles) {
        [void][ListaryOpenVerifyHookNativeMethods]::PostMessage($dialogHandle, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    }
}

function Test-HookModule {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedModuleName
    )

    $processName = $Process.ProcessName
    $processId = $Process.Id
    $hookLoaded = $false
    $hookModulePath = $null
    $status = "HookMissing"

    try {
        $Process.Refresh()

        if ($Process.HasExited) {
            $status = "ProcessExited"
            $hookModulePath = "process exited before module verification"

            return [pscustomobject]@{
                ProcessName = $processName
                Id = $processId
                HookLoaded = $hookLoaded
                HookModulePath = $hookModulePath
                Status = $status
            }
        }

        $modules = $Process.Modules
        if ($null -eq $modules) {
            $status = "ModuleEnumerationFailed"
            $hookModulePath = "module enumeration failed: modules unavailable"

            return [pscustomobject]@{
                ProcessName = $processName
                Id = $processId
                HookLoaded = $hookLoaded
                HookModulePath = $hookModulePath
                Status = $status
            }
        }

        foreach ($module in $modules) {
            if ([string]::Equals($module.ModuleName, $ExpectedModuleName, [System.StringComparison]::OrdinalIgnoreCase)) {
                $hookLoaded = $true
                $hookModulePath = $module.FileName
                $status = "HookLoaded"
                break
            }
        }
    }
    catch {
        $status = "ModuleEnumerationFailed"
        $hookModulePath = "module enumeration failed: $($_.Exception.Message)"
    }

    [pscustomobject]@{
        ProcessName = $processName
        Id = $processId
        HookLoaded = $hookLoaded
        HookModulePath = $hookModulePath
        Status = $status
    }
}

if ($AllMatchingProcesses) {
    $targetProcesses = @(Get-AllMatchingTargetProcesses -TargetNames $ProcessNames)
}
else {
    $dialogOwnerTargets = @(Get-DialogOwnerTargetProcesses -TargetNames $ProcessNames)

    if (-not $SkipDialogPing) {
        foreach ($dialogOwnerTarget in $dialogOwnerTargets) {
            Invoke-DialogPing -DialogHandles $dialogOwnerTarget.DialogHandles
        }

        if ($dialogOwnerTargets.Count -gt 0) {
            Start-Sleep -Milliseconds 250
        }
    }

    $targetProcesses = @($dialogOwnerTargets | ForEach-Object { $_.Process })
}

$results = @(
    foreach ($process in $targetProcesses) {
        Test-HookModule -Process $process -ExpectedModuleName $ModuleName
    }
)

if ($results.Count -eq 0) {
    throw "No target processes found."
}

$results | Format-Table -AutoSize

$verificationFailures = $results | Where-Object { $_.Status -eq "ProcessExited" -or $_.Status -eq "ModuleEnumerationFailed" }
if ($verificationFailures) {
    throw "Hook module could not be verified for one or more target processes."
}

$missingHook = $results | Where-Object { $_.Status -eq "HookMissing" }
if ($missingHook) {
    throw "Hook module missing from one or more target processes."
}
