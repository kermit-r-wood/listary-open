param(
    [string[]]$ProcessNames = @("Antigravity", "notepad++", "chrome", "firefox", "MobaXterm"),
    [string]$ModuleName = "ListaryOpen.Hook.dll"
)

$ErrorActionPreference = "Stop"

$results = @(
    foreach ($name in $ProcessNames) {
        $processes = Get-Process -Name $name -ErrorAction SilentlyContinue

        foreach ($process in $processes) {
            $processName = $process.ProcessName
            $processId = $process.Id
            $hookLoaded = $false
            $hookModulePath = $null
            $status = "HookMissing"

            try {
                $process.Refresh()

                if ($process.HasExited) {
                    $status = "ProcessExited"
                    $hookModulePath = "process exited before module verification"

                    [pscustomobject]@{
                        ProcessName = $processName
                        Id = $processId
                        HookLoaded = $hookLoaded
                        HookModulePath = $hookModulePath
                        Status = $status
                    }

                    continue
                }

                $modules = $process.Modules
                if ($null -eq $modules) {
                    $status = "ModuleEnumerationFailed"
                    $hookModulePath = "module enumeration failed: modules unavailable"

                    [pscustomobject]@{
                        ProcessName = $processName
                        Id = $processId
                        HookLoaded = $hookLoaded
                        HookModulePath = $hookModulePath
                        Status = $status
                    }

                    continue
                }

                foreach ($module in $modules) {
                    if ([string]::Equals($module.ModuleName, $ModuleName, [System.StringComparison]::OrdinalIgnoreCase)) {
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
