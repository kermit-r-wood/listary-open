param(
    [string[]]$ProcessNames = @("Antigravity", "notepad++", "chrome", "firefox", "MobaXterm"),
    [string]$ModuleName = "ListaryOpen.Hook.dll"
)

$ErrorActionPreference = "Stop"

$results = @(
    foreach ($name in $ProcessNames) {
        $processes = Get-Process -Name $name -ErrorAction SilentlyContinue

        foreach ($process in $processes) {
            $hookLoaded = $false
            $hookModulePath = $null

            try {
                foreach ($module in $process.Modules) {
                    if ([string]::Equals($module.ModuleName, $ModuleName, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $hookLoaded = $true
                        $hookModulePath = $module.FileName
                        break
                    }
                }
            }
            catch {
                $hookModulePath = "module enumeration failed: $($_.Exception.Message)"
            }

            [pscustomobject]@{
                ProcessName = $process.ProcessName
                Id = $process.Id
                HookLoaded = $hookLoaded
                HookModulePath = $hookModulePath
            }
        }
    }
)

if ($results.Count -eq 0) {
    throw "No target processes found."
}

$results | Format-Table -AutoSize

$missingHook = $results | Where-Object { -not $_.HookLoaded }
if ($missingHook) {
    throw "Hook module missing from one or more target processes."
}
