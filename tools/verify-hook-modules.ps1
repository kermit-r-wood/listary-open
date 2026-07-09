param(
    [string[]]$ProcessNames = @(
        "Antigravity",
        "notepad++",
        "chrome",
        "msedge",
        "firefox",
        "Code",
        "MobaXterm",
        "mspaint",
        "wordpad",
        "powershell_ise",
        "vlc"),
    [string]$ModuleName = "ListaryOpen.Hook.dll",
    [string]$ExpectedHooksRoot = (Join-Path $PSScriptRoot "..\src\ListaryOpen.App\bin\Debug\net8.0-windows\hooks"),
    [switch]$AllowSubset,
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

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        if (Test-TargetProcessName -ProcessName $process.ProcessName -TargetNames $TargetNames) {
            $process
        }
    }
}

function Get-MissingRequiredTargetRows {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$TargetNames,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$DialogOwnerTargets
    )

    foreach ($targetName in $TargetNames) {
        if ([string]::IsNullOrWhiteSpace($targetName)) {
            continue
        }

        $found = $false
        foreach ($dialogOwnerTarget in $DialogOwnerTargets) {
            if (Test-TargetProcessName -ProcessName $dialogOwnerTarget.Process.ProcessName -TargetNames @($targetName)) {
                $found = $true
                break
            }
        }

        if (-not $found) {
            [pscustomobject]@{
                ProcessName = $targetName
                Id = $null
                HookLoaded = $false
                HookModulePath = "visible dialog-owner process not found"
                Status = "RequiredTargetMissing"
            }
        }
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

function Test-PathUnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or [string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }

    try {
        $fullPath = [System.IO.Path]::GetFullPath($Path)
        $fullRoot = [System.IO.Path]::GetFullPath($Root)
        $directorySeparators = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
        $trimmedRoot = $fullRoot.TrimEnd($directorySeparators)
        $rootWithSeparator = $trimmedRoot + [System.IO.Path]::DirectorySeparatorChar

        return [string]::Equals($fullPath, $trimmedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Get-ProcessStartTimeOrNull {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process
    )

    try {
        return $Process.StartTime
    }
    catch {
        return $null
    }
}

function Test-SameProcessInstance {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$OriginalProcess,
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$LiveProcess,
        [AllowNull()]
        [object]$OriginalStartTime
    )

    if (-not [string]::Equals($OriginalProcess.ProcessName, $LiveProcess.ProcessName, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if ($null -eq $OriginalStartTime) {
        return $true
    }

    $liveStartTime = Get-ProcessStartTimeOrNull -Process $LiveProcess
    return $null -ne $liveStartTime -and $liveStartTime -eq $OriginalStartTime
}

function Test-HookModule {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedModuleName,
        [Parameter(Mandatory = $true)]
        [string]$ExpectedHooksRoot
    )

    $processName = $Process.ProcessName
    $processId = $Process.Id
    $processStartTime = Get-ProcessStartTimeOrNull -Process $Process
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

        $liveProcess = Get-Process -Id $processId -ErrorAction Stop
        $liveProcess.Refresh()
        if (-not (Test-SameProcessInstance -OriginalProcess $Process -LiveProcess $liveProcess -OriginalStartTime $processStartTime)) {
            $status = "ProcessIdentityChanged"
            $hookModulePath = "process changed before module verification"

            return [pscustomobject]@{
                ProcessName = $processName
                Id = $processId
                HookLoaded = $hookLoaded
                HookModulePath = $hookModulePath
                Status = $status
            }
        }

        if ($liveProcess.HasExited) {
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

        $modules = $liveProcess.Modules
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
                $hookModulePath = $module.FileName
                if (Test-PathUnderRoot -Path $hookModulePath -Root $ExpectedHooksRoot) {
                    $hookLoaded = $true
                    $status = "HookLoaded"
                }
                else {
                    $status = "HookPathMismatch"
                }

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
    $requiredTargetRows = @()
}
else {
    $dialogOwnerTargets = @(Get-DialogOwnerTargetProcesses -TargetNames $ProcessNames)
    if ($AllowSubset) {
        $requiredTargetRows = @()
    }
    else {
        $requiredTargetRows = @(Get-MissingRequiredTargetRows -TargetNames $ProcessNames -DialogOwnerTargets $dialogOwnerTargets)
    }

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
        Test-HookModule -Process $process -ExpectedModuleName $ModuleName -ExpectedHooksRoot $ExpectedHooksRoot
    }

    foreach ($requiredTargetRow in $requiredTargetRows) {
        $requiredTargetRow
    }
)

if ($results.Count -eq 0) {
    throw "No target processes found."
}

$results | Format-Table -AutoSize

$missingRequiredTargets = $results | Where-Object { $_.Status -eq "RequiredTargetMissing" }
if ($missingRequiredTargets) {
    throw "Required target dialog owner processes were not found."
}

$verificationFailures = $results | Where-Object { $_.Status -eq "ProcessExited" -or $_.Status -eq "ModuleEnumerationFailed" }
if ($verificationFailures) {
    throw "Hook module could not be verified for one or more target processes."
}

$identityFailures = $results | Where-Object { $_.Status -eq "ProcessIdentityChanged" }
if ($identityFailures) {
    throw "Target process identity changed before module verification."
}

$hookPathMismatch = $results | Where-Object { $_.Status -eq "HookPathMismatch" }
if ($hookPathMismatch) {
    throw "Hook module path did not match expected hooks root for one or more target processes."
}

$missingHook = $results | Where-Object { $_.Status -eq "HookMissing" }
if ($missingHook) {
    throw "Hook module missing from one or more target processes."
}
