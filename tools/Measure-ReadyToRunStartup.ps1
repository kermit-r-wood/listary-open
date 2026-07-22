param(
    [ValidateRange(3, 100)]
    [int]$Samples = 20
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'ListaryOpen.UiPerformanceBenchmark\ListaryOpen.UiPerformanceBenchmark.csproj'
$outputRoot = Join-Path $repositoryRoot 'artifacts\benchmarks\ready-to-run'
$offDirectory = Join-Path $outputRoot 'off'
$onDirectory = Join-Path $outputRoot 'on'

dotnet publish $project -c Release -r win-x64 --self-contained false --nologo `
    -p:BuildNativeHooks=false -p:PublishReadyToRun=false -o $offDirectory
if ($LASTEXITCODE -ne 0) { throw 'ReadyToRun-off publish failed.' }

dotnet publish $project -c Release -r win-x64 --self-contained false --nologo `
    -p:BuildNativeHooks=false -p:PublishReadyToRun=true -o $onDirectory
if ($LASTEXITCODE -ne 0) { throw 'ReadyToRun-on publish failed.' }

$offExecutable = Join-Path $offDirectory 'ListaryOpen.UiPerformanceBenchmark.exe'
$onExecutable = Join-Path $onDirectory 'ListaryOpen.UiPerformanceBenchmark.exe'

function Measure-Startup([string]$executable) {
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $executable -ArgumentList '--startup-probe' -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    $stopwatch.Stop()
    if ($process.ExitCode -ne 0) {
        throw "Startup probe failed with code $($process.ExitCode): $executable"
    }

    $stopwatch.Elapsed.TotalMilliseconds
}

# Warm the OS file cache and apphost/runtime resolution before collecting alternating samples.
[void](Measure-Startup $offExecutable)
[void](Measure-Startup $onExecutable)
$offSamples = @()
$onSamples = @()
for ($iteration = 0; $iteration -lt $Samples; $iteration++) {
    if ($iteration % 2 -eq 0) {
        $offSamples += Measure-Startup $offExecutable
        $onSamples += Measure-Startup $onExecutable
    }
    else {
        $onSamples += Measure-Startup $onExecutable
        $offSamples += Measure-Startup $offExecutable
    }
}

function Summarize([string]$variant, [string]$directory, [double[]]$values) {
    $sorted = @($values | Sort-Object)
    $median = $sorted[[Math]::Floor($sorted.Count / 2)]
    $p95Index = [Math]::Min($sorted.Count - 1, [Math]::Ceiling($sorted.Count * 0.95) - 1)
    [pscustomobject]@{
        Variant = $variant
        Samples = $values.Count
        MedianMilliseconds = [Math]::Round($median, 3)
        P95Milliseconds = [Math]::Round($sorted[$p95Index], 3)
        PublishedBytes = (Get-ChildItem -LiteralPath $directory -File -Recurse | Measure-Object Length -Sum).Sum
    }
}

Summarize 'ReadyToRun-off' $offDirectory $offSamples
Summarize 'ReadyToRun-on' $onDirectory $onSamples
