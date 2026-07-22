param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineHost,
    [Parameter(Mandatory = $true)]
    [string]$OptimizedHost,
    [Parameter(Mandatory = $true)]
    [string]$HookDll,
    [ValidateRange(1, 60)]
    [int]$Seconds = 8,
    [ValidateRange(1, 20)]
    [int]$Repeats = 3
)

$ErrorActionPreference = 'Stop'
$baselinePath = (Resolve-Path -LiteralPath $BaselineHost).Path
$optimizedPath = (Resolve-Path -LiteralPath $OptimizedHost).Path
$hookDllPath = (Resolve-Path -LiteralPath $HookDll).Path
$runtimeDirectory = Split-Path -Parent $hookDllPath
$originalPath = $env:PATH

function Measure-Host {
    param([string]$Label, [string]$Executable)

    $samples = @()
    for ($iteration = 1; $iteration -le $Repeats; $iteration++) {
        $pipeName = "listary-benchmark-$Label-$([Guid]::NewGuid().ToString('N'))"
        $process = Start-Process `
            -FilePath $Executable `
            -ArgumentList @(
                '--parent-pid', "$PID",
                '--secret', 'benchmark',
                '--pipe', $pipeName,
                '--dll', $hookDllPath) `
            -WindowStyle Hidden `
            -PassThru
        try {
            Start-Sleep -Milliseconds 500
            if ($process.HasExited) {
                throw "$Label HookHost exited early with code $($process.ExitCode)."
            }

            $process.Refresh()
            $before = $process.TotalProcessorTime.TotalMilliseconds
            Start-Sleep -Seconds $Seconds
            $process.Refresh()
            $cpuMilliseconds = $process.TotalProcessorTime.TotalMilliseconds - $before
            $samples += $cpuMilliseconds
            [pscustomobject]@{
                Variant = $Label
                Iteration = $iteration
                WallMilliseconds = $Seconds * 1000
                CpuMilliseconds = [Math]::Round($cpuMilliseconds, 3)
            }
        }
        finally {
            if (!$process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
    }

    $sorted = @($samples | Sort-Object)
    [pscustomobject]@{
        Variant = "$Label-median"
        Iteration = 0
        WallMilliseconds = $Seconds * 1000
        CpuMilliseconds = [Math]::Round($sorted[[Math]::Floor($sorted.Count / 2)], 3)
    }
}

try {
    $env:PATH = "$runtimeDirectory;$originalPath"
    Measure-Host -Label 'baseline' -Executable $baselinePath
    Measure-Host -Label 'optimized' -Executable $optimizedPath
}
finally {
    $env:PATH = $originalPath
}
