[CmdletBinding()]
param(
    [string]$PackageDirectory = "",
    [string]$ResultsDirectory = "",
    [switch]$SkipRestore,
    [switch]$SkipDesktopE2E
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot "artifacts\ListaryOpen"
}
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repositoryRoot "artifacts\acceptance-results"
}

$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$zipPath = Join-Path $artifactsRoot "ListaryOpen-win-x64.zip"

$windowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$windowsPrincipal = [Security.Principal.WindowsPrincipal]::new($windowsIdentity)
if (-not $SkipDesktopE2E -and
    -not $windowsPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $windowsIdentity.Dispose()
    throw "Complete acceptance requires an elevated runner for real Task Manager and packaged-app black-box E2E. Start PowerShell as administrator."
}
$windowsIdentity.Dispose()

function Test-PathUnderRoot {
    param([string]$Path, [string]$Root)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    return $fullPath -eq $fullRoot -or
        $fullPath.StartsWith($fullRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

if (-not (Test-PathUnderRoot -Path $PackageDirectory -Root $artifactsRoot) -or
    -not (Test-PathUnderRoot -Path $ResultsDirectory -Root $artifactsRoot)) {
    throw "Package and results directories must remain under '$artifactsRoot'."
}

if ($PackageDirectory.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $ResultsDirectory.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package and results directories must be children of '$artifactsRoot', not the artifacts root itself."
}

if ($PackageDirectory.Equals($ResultsDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "PackageDirectory and ResultsDirectory must be different directories."
}

$packageDataDirectory = [IO.Path]::GetFullPath((Join-Path $PackageDirectory "data"))

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

function Remove-PathWithRetry {
    param([Parameter(Mandatory = $true)][string]$Path)

    $lastError = $null
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_
            if ($attempt -lt 20) {
                Start-Sleep -Milliseconds 250
            }
        }
    }

    throw "Could not remove '$Path' after waiting 5 seconds for transient file handles: $($lastError.Exception.Message)"
}

function Remove-DirectoryWithRetry {
    param([Parameter(Mandatory = $true)][string]$Path)

    Remove-PathWithRetry -Path $Path
}

function Clear-PackageDirectoryPreservingData {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DataDirectory
    )

    if (-not (Test-Path -LiteralPath $PackagePath)) {
        New-Item -ItemType Directory -Path $PackagePath -Force | Out-Null
        return
    }

    $fullPackagePath = [IO.Path]::GetFullPath($PackagePath)
    $fullDataDirectory = [IO.Path]::GetFullPath($DataDirectory)
    if (-not (Test-PathUnderRoot -Path $fullDataDirectory -Root $fullPackagePath) -or
        $fullDataDirectory.Equals($fullPackagePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The preserved data directory must be a child of PackageDirectory."
    }

    foreach ($item in Get-ChildItem -LiteralPath $fullPackagePath -Force) {
        $fullItemPath = [IO.Path]::GetFullPath($item.FullName)
        if ($item.PSIsContainer -and
            $fullItemPath.Equals($fullDataDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Preserving indexed data directory: $fullDataDirectory"
            continue
        }

        if (-not (Test-PathUnderRoot -Path $fullItemPath -Root $fullPackagePath) -or
            $fullItemPath.Equals($fullPackagePath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove package item outside PackageDirectory: '$fullItemPath'."
        }

        Remove-PathWithRetry -Path $fullItemPath
    }
}

function Get-PackageFilesForArchive {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DataDirectory
    )

    return @(
        Get-ChildItem -LiteralPath $PackagePath -Recurse -Force -File |
            Where-Object { -not (Test-PathUnderRoot -Path $_.FullName -Root $DataDirectory) }
    )
}

function Compress-PackageArchive {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$DataDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $fullDataDirectory = [IO.Path]::GetFullPath($DataDirectory)
    $archiveItems = @(
        Get-ChildItem -LiteralPath $PackagePath -Force |
            Where-Object {
                -not ([IO.Path]::GetFullPath($_.FullName).Equals(
                    $fullDataDirectory,
                    [StringComparison]::OrdinalIgnoreCase))
            } |
            ForEach-Object { $_.FullName }
    )

    if ($archiveItems.Count -eq 0) {
        throw "No distributable files were found in '$PackagePath'."
    }

    Compress-Archive -LiteralPath $archiveItems -DestinationPath $DestinationPath -Force
}

Clear-PackageDirectoryPreservingData -PackagePath $PackageDirectory -DataDirectory $packageDataDirectory

if (Test-Path -LiteralPath $ResultsDirectory) {
    Remove-DirectoryWithRetry -Path $ResultsDirectory
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [switch]$AllowFailure
    )

    Write-Host "[$Name] $FilePath $($Arguments -join ' ')"
    Push-Location $WorkingDirectory
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        try {
            # Windows PowerShell 5.1 wraps a native process' stderr as
            # NativeCommandError. Native tools such as Cargo routinely write
            # successful progress to stderr, so success must be decided by the
            # process exit code while retaining both streams in the log.
            $ErrorActionPreference = "Continue"
            $output = & $FilePath @Arguments 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        $output | Tee-Object -FilePath $LogPath | ForEach-Object { Write-Host $_ }
        if ($exitCode -ne 0) {
            if ($AllowFailure) {
                Write-Warning "$Name failed with exit code $exitCode; retaining the failure as advisory evidence from the CI desktop session. See '$LogPath'."
                return $false
            }
            throw "$Name failed with exit code $exitCode. See '$LogPath'."
        }
        if ($AllowFailure) {
            return $true
        }
    }
    finally {
        Pop-Location
    }
}

function Enable-PortableRustHostIfNeeded {
    if (Get-Command link.exe -ErrorAction SilentlyContinue) {
        return
    }

    $toolchains = (& rustup toolchain list 2>$null) -join "`n"
    if ($toolchains -notmatch "stable-x86_64-pc-windows-gnu") {
        return
    }

    $gnuToolchain = "stable-x86_64-pc-windows-gnu"
    $gnuSysroot = (& rustc "+$gnuToolchain" --print sysroot).Trim()
    $gnuLinker = Join-Path $gnuSysroot "lib\rustlib\x86_64-pc-windows-gnu\bin\rust-lld.exe"
    if (-not (Test-Path -LiteralPath $gnuLinker)) {
        return
    }

    $env:RUSTUP_TOOLCHAIN = $gnuToolchain
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = $gnuLinker
    Write-Host "Using the installed GNU Rust host and bundled linker because MSVC link.exe is unavailable."
}

Enable-PortableRustHostIfNeeded

$nativeMingwRoot = $null
foreach ($candidate in @(
    $(if ($env:SCOOP) { Join-Path $env:SCOOP "apps\mingw-mstorsjo-llvm-msvcrt\current" }),
    (Join-Path $env:USERPROFILE "scoop\apps\mingw-mstorsjo-llvm-msvcrt\current"))) {
    if ($candidate -and (Test-Path -LiteralPath $candidate)) {
        $nativeMingwRoot = $candidate
        break
    }
}

if ($nativeMingwRoot) {
    $x64Linker = Join-Path $nativeMingwRoot "bin\x86_64-w64-mingw32-clang.exe"
    $x86Linker = Join-Path $nativeMingwRoot "bin\i686-w64-mingw32-clang.exe"
    if (-not (Test-Path -LiteralPath $x64Linker) -or -not (Test-Path -LiteralPath $x86Linker)) {
        throw "The detected LLVM-MinGW installation is missing one or both gnullvm linkers under '$nativeMingwRoot\bin'."
    }

    $env:CARGO_TARGET_X86_64_PC_WINDOWS_GNULLVM_LINKER = $x64Linker
    $env:CARGO_TARGET_I686_PC_WINDOWS_GNULLVM_LINKER = $x86Linker
}

function Invoke-NativeTests {
    param(
        [Parameter(Mandatory = $true)][string]$Architecture,
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $originalPath = $env:PATH
    try {
        if ($nativeMingwRoot) {
            $runtimeDirectory = Join-Path $nativeMingwRoot "$Architecture-w64-mingw32\bin"
            if (Test-Path -LiteralPath $runtimeDirectory) {
                $env:PATH = "$runtimeDirectory;$originalPath"
            }
        }

        Invoke-LoggedCommand -Name "Native hook tests $Architecture" -FilePath "cargo" `
            -Arguments @("test", "--workspace", "--locked", "--target", $Target) `
            -WorkingDirectory $nativeRoot `
            -LogPath $LogPath
    }
    finally {
        $env:PATH = $originalPath
    }
}

$solutionPath = Join-Path $repositoryRoot "ListaryOpen.sln"
$integrationProject = Join-Path $repositoryRoot "tests\ListaryOpen.IntegrationTests\ListaryOpen.IntegrationTests.csproj"
$testHostProject = Join-Path $repositoryRoot "tests\ListaryOpen.TestHost\ListaryOpen.TestHost.csproj"
$coreTests = Join-Path $repositoryRoot "tests\ListaryOpen.Core.Tests\ListaryOpen.Core.Tests.csproj"
$infrastructureTests = Join-Path $repositoryRoot "tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj"
$appProject = Join-Path $repositoryRoot "src\ListaryOpen.App\ListaryOpen.App.csproj"
$previewHostProject = Join-Path $repositoryRoot "src\ListaryOpen.PreviewHost\ListaryOpen.PreviewHost.csproj"
$nativeRoot = Join-Path $repositoryRoot "native\ListaryOpen.Hooks"

if (-not $SkipRestore) {
    Invoke-LoggedCommand -Name "Restore solution" -FilePath "dotnet" `
        -Arguments @("restore", $solutionPath, "-r", "win-x64", "-p:Configuration=Release", "-p:PublishReadyToRun=true") `
        -WorkingDirectory $repositoryRoot `
        -LogPath (Join-Path $ResultsDirectory "restore-solution.log")
    Invoke-LoggedCommand -Name "Restore desktop integration tests" -FilePath "dotnet" `
        -Arguments @("restore", $integrationProject) `
        -WorkingDirectory $repositoryRoot `
        -LogPath (Join-Path $ResultsDirectory "restore-integration.log")
    Invoke-LoggedCommand -Name "Restore win-x64 publish projects" -FilePath "dotnet" `
        -Arguments @("restore", $appProject, "-r", "win-x64", "-p:Configuration=Release", "-p:PublishReadyToRun=true") `
        -WorkingDirectory $repositoryRoot `
        -LogPath (Join-Path $ResultsDirectory "restore-publish.log")
}

Invoke-NativeTests -Architecture "x86_64" -Target "x86_64-pc-windows-gnullvm" `
    -LogPath (Join-Path $ResultsDirectory "native-x64.log")
Invoke-NativeTests -Architecture "i686" -Target "i686-pc-windows-gnullvm" `
    -LogPath (Join-Path $ResultsDirectory "native-x86.log")

Invoke-LoggedCommand -Name "Core tests" -FilePath "dotnet" `
    -Arguments @(
        "test", $coreTests, "-c", "Release", "--no-restore", "--nologo",
        "-p:BuildNativeHooks=false",
        "--results-directory", $ResultsDirectory,
        "--logger", "trx;LogFileName=core.trx") `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "core-tests.log")
Invoke-LoggedCommand -Name "Infrastructure and WPF tests" -FilePath "dotnet" `
    -Arguments @(
        "test", $infrastructureTests, "-c", "Release", "--no-restore", "--nologo",
        "-p:BuildNativeHooks=false",
        "--results-directory", $ResultsDirectory,
        "--logger", "trx;LogFileName=infrastructure.trx") `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "infrastructure-tests.log")

$screenshotDirectory = Join-Path $ResultsDirectory "visual-evidence"
$env:LISTARYOPEN_SCREENSHOT_DIR = $screenshotDirectory

Invoke-LoggedCommand -Name "Publish win-x64 app with native hooks" -FilePath "dotnet" `
    -Arguments @(
        "publish", $appProject,
        "-c", "Release", "-r", "win-x64", "--self-contained", "false", "--no-restore",
        "-p:BuildNativeHooks=true", "-o", $PackageDirectory) `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "publish.log")

Invoke-LoggedCommand -Name "Publish isolated preview host" -FilePath "dotnet" `
    -Arguments @(
        "publish", $previewHostProject,
        "-c", "Release", "-r", "win-x64", "--self-contained", "false", "--no-restore",
        "-p:BuildNativeHooks=false", "-o", $PackageDirectory) `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "publish-preview-host.log")

$requiredArtifacts = @(
    "ListaryOpen.App.exe",
    "ListaryOpen.App.dll",
    "ListaryOpen.App.deps.json",
    "ListaryOpen.App.runtimeconfig.json",
    "ListaryOpen.PreviewHost.exe",
    "ListaryOpen.PreviewHost.dll",
    "ListaryOpen.PreviewHost.deps.json",
    "ListaryOpen.PreviewHost.runtimeconfig.json",
    "ListaryOpen.Core.dll",
    "ListaryOpen.Infrastructure.dll",
    "ListaryOpen.Indexer.Elevated.exe",
    "ListaryOpen.Indexer.Elevated.dll",
    "ListaryOpen.Indexer.Elevated.deps.json",
    "ListaryOpen.Indexer.Elevated.runtimeconfig.json",
    "e_sqlite3.dll",
    "MsgReader.dll",
    "XstReader.Api.dll",
    "OpenMcdf.dll",
    "RtfPipe.dll",
    "UtfUnknown.dll",
    "Microsoft.IO.RecyclableMemoryStream.dll",
    "Microsoft.Maui.Graphics.dll",
    "Microsoft.Bcl.Cryptography.dll",
    "System.Formats.Asn1.dll",
    "System.Security.Cryptography.Pkcs.dll",
    "SharpCompress.dll",
    "ExcelDataReader.dll",
    "K4os.Compression.LZ4.Streams.dll",
    "K4os.Compression.LZ4.dll",
    "K4os.Hash.xxHash.dll",
    "System.IO.Pipelines.dll",
    "plist-cil.dll",
    "pdfium.dll",
    "THIRD-PARTY-NOTICES.txt",
    "hooks/x64/ListaryOpen.HookHost.exe",
    "hooks/x64/ListaryOpen.Hook.dll",
    "hooks/x64/libunwind.dll",
    "hooks/x86/ListaryOpen.HookHost.exe",
    "hooks/x86/ListaryOpen.Hook.dll",
    "hooks/x86/libunwind.dll"
)
foreach ($relativePath in $requiredArtifacts) {
    $fullPath = Join-Path $PackageDirectory ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required package file is missing: $fullPath"
    }
    if ((Get-Item -LiteralPath $fullPath).Length -le 0) {
        throw "Required package file is empty: $fullPath"
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Package executable is not a PE image: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) {
        throw "Package executable has an invalid PE header: $Path"
    }

    return [BitConverter]::ToUInt16($bytes, $peOffset + 4)
}

foreach ($relativePath in @(
    "ListaryOpen.App.exe",
    "ListaryOpen.PreviewHost.exe",
    "ListaryOpen.Indexer.Elevated.exe",
    "e_sqlite3.dll",
    "pdfium.dll",
    "hooks/x64/ListaryOpen.HookHost.exe",
    "hooks/x64/ListaryOpen.Hook.dll",
    "hooks/x64/libunwind.dll")) {
    $path = Join-Path $PackageDirectory ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    if ((Get-PeMachine -Path $path) -ne 0x8664) {
        throw "Expected AMD64 PE machine for '$relativePath'."
    }
}
foreach ($relativePath in @(
    "hooks/x86/ListaryOpen.HookHost.exe",
    "hooks/x86/ListaryOpen.Hook.dll",
    "hooks/x86/libunwind.dll")) {
    $path = Join-Path $PackageDirectory ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    if ((Get-PeMachine -Path $path) -ne 0x014c) {
        throw "Expected I386 PE machine for '$relativePath'."
    }
}

if ($SkipDesktopE2E) {
    Compress-PackageArchive `
        -PackagePath $PackageDirectory `
        -DataDirectory $packageDataDirectory `
        -DestinationPath $zipPath
    [pscustomobject]@{
        SchemaVersion = 1
        GeneratedAtUtc = [DateTimeOffset]::UtcNow
        DesktopE2ESkipped = $true
        Reason = "Desktop E2E requires an interactive elevated Windows session and optional third-party applications such as Firefox."
        PackagePath = $zipPath
        PackageSha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ResultsDirectory "ci-package-report.json") -Encoding utf8
    Write-Host "CI package validation completed without desktop E2E: $zipPath"
    return
}

if (-not ("ListaryOpenAcceptance.NativeProcessProbe" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ListaryOpenAcceptance
{
    public static class NativeProcessProbe
    {
        private const uint SnapshotModule = 0x00000008;
        private const uint SnapshotModule32 = 0x00000010;
        private const int ErrorBadLength = 24;
        private const int ErrorNoMoreFiles = 18;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(
            IntPtr process,
            out ushort processMachine,
            out ushort nativeMachine);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ModuleEntry32
        {
            public uint Size;
            public uint ModuleId;
            public uint ProcessId;
            public uint GlobalUsageCount;
            public uint ProcessUsageCount;
            public IntPtr BaseAddress;
            public uint BaseSize;
            public IntPtr ModuleHandle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string ModuleName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExePath;
        }

        public static string Architecture(Process process)
        {
            ushort processMachine;
            ushort nativeMachine;
            if (!IsWow64Process2(process.Handle, out processMachine, out nativeMachine))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (processMachine == 0x014c)
                return "x86";
            if (processMachine == 0 && nativeMachine == 0x8664)
                return "x64";
            return "other-0x" + processMachine.ToString("x4") + "-0x" + nativeMachine.ToString("x4");
        }

        public static string[] MatchingModulePaths(Process process, string[] targetPaths)
        {
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in targetPaths)
                targets.Add(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            IntPtr snapshot = new IntPtr(-1);
            for (int attempt = 0; attempt < 5; attempt++)
            {
                snapshot = CreateToolhelp32Snapshot(SnapshotModule | SnapshotModule32, (uint)process.Id);
                if (snapshot != new IntPtr(-1) || Marshal.GetLastWin32Error() != ErrorBadLength)
                    break;
            }
            if (snapshot == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateToolhelp32Snapshot failed");

            try
            {
                var entry = new ModuleEntry32 { Size = (uint)Marshal.SizeOf<ModuleEntry32>() };
                if (!Module32FirstW(snapshot, ref entry))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreFiles)
                        return new string[0];
                    throw new Win32Exception(error, "Module32FirstW failed");
                }

                var matches = new List<string>();
                do
                {
                    if (!string.IsNullOrWhiteSpace(entry.ExePath))
                    {
                        string path = Path.GetFullPath(entry.ExePath)
                            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        if (targets.Contains(path))
                            matches.Add(path);
                    }
                    entry.Size = (uint)Marshal.SizeOf<ModuleEntry32>();
                }
                while (Module32NextW(snapshot, ref entry));

                int finalError = Marshal.GetLastWin32Error();
                if (finalError != ErrorNoMoreFiles)
                    throw new Win32Exception(finalError, "Module32NextW failed");
                return matches.ToArray();
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
    }

    public sealed class RestartManagerConsumer
    {
        public int ProcessId { get; set; }
        public long StartTimeFileTimeUtc { get; set; }
        public string ApplicationName { get; set; }
        public uint SessionId { get; set; }
    }

    public static class RestartManagerProbe
    {
        private const int ErrorMoreData = 234;
        private const int MaxPath = 260;
        private const int MaxServiceName = 64;

        [StructLayout(LayoutKind.Sequential)]
        private struct RmUniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RmAppType
        {
            Unknown = 0,
            MainWindow = 1,
            OtherWindow = 2,
            Service = 3,
            Explorer = 4,
            Console = 5,
            Critical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RmProcessInfo
        {
            public RmUniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
            public string ApplicationName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxServiceName)]
            public string ServiceShortName;
            public RmAppType ApplicationType;
            public uint ApplicationStatus;
            public uint SessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Restartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint sessionHandle,
            uint fileCount,
            string[] fileNames,
            uint applicationCount,
            IntPtr applications,
            uint serviceCount,
            string[] serviceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint sessionHandle,
            out uint processInfoNeeded,
            ref uint processInfoCount,
            [In, Out] RmProcessInfo[] affectedApplications,
            ref uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint sessionHandle);

        public static RestartManagerConsumer[] Consumers(string[] paths)
        {
            uint session;
            int result = RmStartSession(out session, 0, Guid.NewGuid().ToString("N"));
            if (result != 0)
                throw new Win32Exception(result, "RmStartSession failed");
            try
            {
                result = RmRegisterResources(session, (uint)paths.Length, paths, 0, IntPtr.Zero, 0, null);
                if (result != 0)
                    throw new Win32Exception(result, "RmRegisterResources failed");

                uint needed;
                uint count = 0;
                uint reasons = 0;
                result = RmGetList(session, out needed, ref count, null, ref reasons);
                if (result == 0 && needed == 0)
                    return new RestartManagerConsumer[0];
                if (result != ErrorMoreData)
                    throw new Win32Exception(result, "RmGetList sizing failed");

                var details = new RmProcessInfo[needed];
                count = needed;
                result = RmGetList(session, out needed, ref count, details, ref reasons);
                if (result != 0)
                    throw new Win32Exception(result, "RmGetList failed");

                var consumers = new List<RestartManagerConsumer>();
                for (int index = 0; index < count; index++)
                {
                    long fileTime = ((long)details[index].Process.ProcessStartTime.dwHighDateTime << 32) |
                        (uint)details[index].Process.ProcessStartTime.dwLowDateTime;
                    consumers.Add(new RestartManagerConsumer
                    {
                        ProcessId = details[index].Process.ProcessId,
                        StartTimeFileTimeUtc = fileTime,
                        ApplicationName = details[index].ApplicationName,
                        SessionId = details[index].SessionId
                    });
                }
                return consumers.ToArray();
            }
            finally
            {
                RmEndSession(session);
            }
        }
    }
}
"@
}

function ConvertTo-NativeAuditPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Get-NativeModuleAuditObservations {
    param(
        [Parameter(Mandatory = $true)][int]$SessionId,
        [Parameter(Mandatory = $true)][string[]]$TargetPaths
    )

    $observations = @()
    foreach ($process in @(Get-Process -ErrorAction SilentlyContinue)) {
        $processId = $process.Id
        $processName = $process.ProcessName
        $observedSessionId = $null
        $enumerationErrors = @()
        try {
            $observedSessionId = $process.SessionId
            if ($observedSessionId -ne $SessionId) {
                $process.Dispose()
                continue
            }
        }
        catch {
            $enumerationErrors += "SessionId: $($_.Exception.Message)"
        }

        $startTimeFileTimeUtc = $null
        $architecture = $null
        $modulePaths = @()
        try {
            $startTimeFileTimeUtc = $process.StartTime.ToUniversalTime().ToFileTimeUtc()
            $architecture = [ListaryOpenAcceptance.NativeProcessProbe]::Architecture($process)
            if ($architecture -notin @("x64", "x86")) {
                continue
            }
            $modulePaths = @([ListaryOpenAcceptance.NativeProcessProbe]::MatchingModulePaths($process, $TargetPaths))
        }
        catch {
            $enumerationErrors += $_.Exception.Message
        }
        finally {
            $process.Dispose()
        }

        $observations += [pscustomobject]@{
            ProcessId = $processId
            ProcessName = $processName
            SessionId = $observedSessionId
            StartTimeFileTimeUtc = $startTimeFileTimeUtc
            Architecture = $architecture
            ModulesEnumerated = $enumerationErrors.Count -eq 0 -and $null -ne $startTimeFileTimeUtc
            EnumerationError = if ($enumerationErrors.Count -eq 0) { $null } else { $enumerationErrors -join "; " }
            ModulePaths = $modulePaths
        }
    }
    return $observations
}

function Get-NativeModuleAuditMatches {
    param(
        [Parameter(Mandatory = $true)][object[]]$Observations,
        [Parameter(Mandatory = $true)][string[]]$TargetPaths
    )

    $matches = @()
    foreach ($observation in @($Observations | Where-Object ModulesEnumerated)) {
        foreach ($modulePath in $observation.ModulePaths) {
            if (@($TargetPaths | Where-Object {
                [string]::Equals($_, $modulePath, [StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0) {
                $matches += [pscustomobject]@{
                    ProcessId = $observation.ProcessId
                    ProcessName = $observation.ProcessName
                    StartTimeFileTimeUtc = $observation.StartTimeFileTimeUtc
                    Architecture = $observation.Architecture
                    ModulePath = $modulePath
                }
            }
        }
    }
    return $matches
}

function Get-BaselineEnumerationRegressions {
    param(
        [Parameter(Mandatory = $true)][object[]]$Baseline,
        [Parameter(Mandatory = $true)][object[]]$Current
    )

    $regressions = @()
    foreach ($baselineProcess in @($Baseline | Where-Object ModulesEnumerated)) {
        $samePid = @($Current | Where-Object ProcessId -eq $baselineProcess.ProcessId)
        if ($samePid.Count -eq 0) {
            continue
        }
        $sameIdentity = @($samePid | Where-Object {
            $null -ne $_.StartTimeFileTimeUtc -and
            $_.StartTimeFileTimeUtc -eq $baselineProcess.StartTimeFileTimeUtc
        })
        if ($sameIdentity.Count -eq 0) {
            $unknownIdentity = @($samePid | Where-Object { $null -eq $_.StartTimeFileTimeUtc })
            if ($unknownIdentity.Count -gt 0) {
                $regressions += [pscustomobject]@{
                    ProcessId = $baselineProcess.ProcessId
                    ProcessName = $baselineProcess.ProcessName
                    StartTimeFileTimeUtc = $baselineProcess.StartTimeFileTimeUtc
                    Reason = "A baseline-enumerable PID still exists but its creation time can no longer be read."
                }
            }
            continue
        }
        foreach ($currentProcess in $sameIdentity) {
            if (-not $currentProcess.ModulesEnumerated) {
                $regressions += [pscustomobject]@{
                    ProcessId = $baselineProcess.ProcessId
                    ProcessName = $baselineProcess.ProcessName
                    StartTimeFileTimeUtc = $baselineProcess.StartTimeFileTimeUtc
                    Reason = "A baseline-enumerable process can no longer be enumerated: $($currentProcess.EnumerationError)"
                }
            }
        }
    }
    return $regressions
}

function Write-NativeModuleAuditBaseline {
    param(
        [Parameter(Mandatory = $true)][string[]]$TargetPaths,
        [Parameter(Mandatory = $true)][int]$SessionId,
        [Parameter(Mandatory = $true)][string]$EvidencePath
    )

    $observations = @(Get-NativeModuleAuditObservations -SessionId $SessionId -TargetPaths $TargetPaths)
    $matches = @(Get-NativeModuleAuditMatches -Observations $observations -TargetPaths $TargetPaths)
    $evidence = [pscustomobject]@{
        SchemaVersion = 1
        CapturedAtUtc = [DateTimeOffset]::UtcNow
        EvidenceFile = [IO.Path]::GetFileName($EvidencePath)
        SessionId = $SessionId
        TargetPaths = $TargetPaths
        EnumerableProcessCount = @($observations | Where-Object ModulesEnumerated).Count
        UnenumerableProcesses = @($observations | Where-Object { -not $_.ModulesEnumerated })
        Matches = $matches
        Processes = $observations
    }
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    if ($matches.Count -gt 0) {
        throw "Native module audit baseline is already contaminated by exact package modules: $($matches | ConvertTo-Json -Compress)"
    }
    return $evidence
}

function Invoke-FinalNativeModuleUnloadAudit {
    param(
        [Parameter(Mandatory = $true)][object]$BaselineEvidence,
        [Parameter(Mandatory = $true)][string[]]$TargetPaths,
        [Parameter(Mandatory = $true)][int]$SessionId,
        [Parameter(Mandatory = $true)][string]$EvidencePath
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $cleanRounds = 0
    $rounds = @()
    do {
        $observations = @(Get-NativeModuleAuditObservations -SessionId $SessionId -TargetPaths $TargetPaths)
        $moduleMatches = @(Get-NativeModuleAuditMatches -Observations $observations -TargetPaths $TargetPaths)
        $enumerationRegressions = @(Get-BaselineEnumerationRegressions -Baseline $BaselineEvidence.Processes -Current $observations)
        $restartManagerConsumers = @()
        $restartManagerError = $null
        try {
            $restartManagerConsumers = @([ListaryOpenAcceptance.RestartManagerProbe]::Consumers($TargetPaths))
        }
        catch {
            $restartManagerError = $_.Exception.Message
        }
        $roundClean = $moduleMatches.Count -eq 0 -and
            $enumerationRegressions.Count -eq 0 -and
            $restartManagerConsumers.Count -eq 0 -and
            $null -eq $restartManagerError
        if ($roundClean) {
            $cleanRounds++
        }
        else {
            $cleanRounds = 0
        }
        $rounds += [pscustomobject]@{
            CapturedAtUtc = [DateTimeOffset]::UtcNow
            EnumerableProcessCount = @($observations | Where-Object ModulesEnumerated).Count
            UnenumerableProcessCount = @($observations | Where-Object { -not $_.ModulesEnumerated }).Count
            ModuleMatches = $moduleMatches
            BaselineEnumerationRegressions = $enumerationRegressions
            RestartManagerConsumers = $restartManagerConsumers
            RestartManagerError = $restartManagerError
            Clean = $roundClean
        }
        if ($cleanRounds -lt 2) {
            Start-Sleep -Milliseconds 250
        }
    } while ($cleanRounds -lt 2 -and [DateTime]::UtcNow -lt $deadline)

    $lastRound = $rounds[-1]
    $passed = $cleanRounds -ge 2
    $evidence = [pscustomobject]@{
        SchemaVersion = 1
        CapturedAtUtc = [DateTimeOffset]::UtcNow
        SessionId = $SessionId
        TargetPaths = $TargetPaths
        BaselineEvidence = $BaselineEvidence.EvidenceFile
        RequiredConsecutiveCleanRounds = 2
        Passed = $passed
        FinalModuleMatches = $lastRound.ModuleMatches
        FinalBaselineEnumerationRegressions = $lastRound.BaselineEnumerationRegressions
        FinalRestartManagerConsumers = $lastRound.RestartManagerConsumers
        FinalRestartManagerError = $lastRound.RestartManagerError
        Rounds = $rounds
    }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    if (-not $passed) {
        throw "Global native module unload audit failed without closing any user process: $($lastRound | ConvertTo-Json -Depth 6 -Compress)"
    }
    return $evidence
}

$nativeModuleAuditTargetPaths = @(
    ConvertTo-NativeAuditPath -Path (Join-Path $PackageDirectory "hooks\x64\ListaryOpen.Hook.dll")
    ConvertTo-NativeAuditPath -Path (Join-Path $PackageDirectory "hooks\x64\libunwind.dll")
    ConvertTo-NativeAuditPath -Path (Join-Path $PackageDirectory "hooks\x86\ListaryOpen.Hook.dll")
    ConvertTo-NativeAuditPath -Path (Join-Path $PackageDirectory "hooks\x86\libunwind.dll")
)
$nativeModuleAuditSessionId = (Get-Process -Id $PID).SessionId
$nativeModuleAuditBaselinePath = Join-Path $ResultsDirectory "native-module-baseline.json"
$nativeModuleAuditBaseline = Write-NativeModuleAuditBaseline `
    -TargetPaths $nativeModuleAuditTargetPaths `
    -SessionId $nativeModuleAuditSessionId `
    -EvidencePath $nativeModuleAuditBaselinePath

$testHostX64Directory = Join-Path $ResultsDirectory "test-hosts\x64"
$testHostX86Directory = Join-Path $ResultsDirectory "test-hosts\x86"
Invoke-LoggedCommand -Name "Publish x64 desktop test host" -FilePath "dotnet" `
    -Arguments @("publish", $testHostProject, "-c", "Release", "-r", "win-x64", "--self-contained", "false", "-o", $testHostX64Directory) `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "publish-test-host-x64.log")
Invoke-LoggedCommand -Name "Publish x86 desktop test host" -FilePath "dotnet" `
    -Arguments @("publish", $testHostProject, "-c", "Release", "-r", "win-x86", "--self-contained", "true", "-o", $testHostX86Directory) `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "publish-test-host-x86.log")

$env:LISTARYOPEN_NATIVE_PACKAGE_DIR = $PackageDirectory
$env:LISTARYOPEN_TEST_HOST_PATH = Join-Path $testHostX64Directory "ListaryOpen.TestHost.exe"
$env:LISTARYOPEN_TEST_HOST_X64_PATH = $env:LISTARYOPEN_TEST_HOST_PATH
$env:LISTARYOPEN_TEST_HOST_X86_PATH = Join-Path $testHostX86Directory "ListaryOpen.TestHost.exe"
Invoke-LoggedCommand -Name "Real Windows desktop integration tests" -FilePath "dotnet" `
    -Arguments @(
        "test", $integrationProject, "-c", "Release", "--no-restore", "--nologo",
        "--filter", "Category=DesktopIntegration",
        "--results-directory", $ResultsDirectory,
        "--logger", "trx;LogFileName=desktop.trx") `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "desktop-tests.log")
$desktopIntegrationPassed = $true

$env:LISTARYOPEN_PACKAGE_DIR = $PackageDirectory
Invoke-LoggedCommand -Name "Elevated real Task Manager integration tests" -FilePath "dotnet" `
    -Arguments @(
        "test", $integrationProject, "-c", "Release", "--no-restore", "--nologo",
        "--filter", "Category=ElevatedDesktopIntegration",
        "--results-directory", $ResultsDirectory,
        "--logger", "trx;LogFileName=elevated-desktop.trx") `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "elevated-desktop-tests.log")
Invoke-LoggedCommand -Name "Elevated NTFS indexer integration tests" -FilePath "dotnet" `
    -Arguments @(
        "test", $integrationProject, "-c", "Release", "--no-restore", "--nologo",
        "--filter", "Category=ElevatedIndexerIntegration",
        "--results-directory", $ResultsDirectory,
        "--logger", "trx;LogFileName=elevated-indexer.trx") `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "elevated-indexer-tests.log")

# The real helper creates its trusted data\tmp directory beside the published
# executable even though the integration tests remove every temporary output
# file. Packaged black-box tests require a pristine first-run directory, so
# remove only this verified-empty runtime tree. Never hide actual leaked data.
if (Test-Path -LiteralPath $packageDataDirectory -PathType Container) {
    $runtimeFiles = @(Get-ChildItem -LiteralPath $packageDataDirectory -Recurse -Force -File)
    if ($runtimeFiles.Count -gt 0) {
        throw "Elevated indexer tests left runtime files in the package: $($runtimeFiles[0].FullName)"
    }

    Get-ChildItem -LiteralPath $packageDataDirectory -Recurse -Force -Directory |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
    Remove-Item -LiteralPath $packageDataDirectory -Force
}
Invoke-LoggedCommand -Name "Elevated packaged-app black-box E2E" -FilePath "dotnet" `
    -Arguments @(
        "test", $integrationProject, "-c", "Release", "--no-restore", "--nologo",
        "--filter", "Category=ElevatedPackagedBlackboxE2E",
        "--results-directory", $ResultsDirectory,
        "--logger", "trx;LogFileName=packaged-blackbox.trx") `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $ResultsDirectory "packaged-blackbox-tests.log")

$nativeModuleUnloadAuditPath = Join-Path $ResultsDirectory "native-module-unload-audit.json"
$globalNativeModuleUnloadAudit = Invoke-FinalNativeModuleUnloadAudit `
    -BaselineEvidence $nativeModuleAuditBaseline `
    -TargetPaths $nativeModuleAuditTargetPaths `
    -SessionId $nativeModuleAuditSessionId `
    -EvidencePath $nativeModuleUnloadAuditPath

$desktopVisualMatrixPath = Join-Path $repositoryRoot "tests\desktop-visual-evidence.json"
if (-not (Test-Path -LiteralPath $desktopVisualMatrixPath -PathType Leaf)) {
    throw "Authoritative desktop visual evidence matrix is missing: $desktopVisualMatrixPath"
}
$desktopVisualMatrix = Get-Content -LiteralPath $desktopVisualMatrixPath -Raw | ConvertFrom-Json
if ($desktopVisualMatrix.schemaVersion -ne 2 -or $desktopVisualMatrix.artifacts.Count -eq 0) {
    throw "Authoritative desktop visual evidence matrix is empty or unsupported."
}
$duplicateDesktopVisualIds = @($desktopVisualMatrix.artifacts | Group-Object id | Where-Object Count -ne 1)
$duplicateDesktopVisualFiles = @($desktopVisualMatrix.artifacts | Group-Object file | Where-Object Count -ne 1)
if ($duplicateDesktopVisualIds.Count -gt 0 -or $duplicateDesktopVisualFiles.Count -gt 0) {
    throw "Authoritative desktop visual evidence matrix contains duplicate ids or files."
}
foreach ($artifact in $desktopVisualMatrix.artifacts) {
    if ([string]::IsNullOrWhiteSpace($artifact.id) -or
        [string]::IsNullOrWhiteSpace($artifact.file) -or
        [string]::IsNullOrWhiteSpace($artifact.suite) -or
        [string]::IsNullOrWhiteSpace($artifact.testClass) -or
        [string]::IsNullOrWhiteSpace($artifact.testMethod) -or
        [string]::IsNullOrWhiteSpace($artifact.evidenceLevel) -or
        $artifact.minimumPassedInstances -le 0) {
        throw "Authoritative desktop visual evidence entry is incomplete: $($artifact | ConvertTo-Json -Compress)"
    }
}

$requiredDesktopScreenshots = @($desktopVisualMatrix.artifacts | ForEach-Object file)
if ($desktopIntegrationPassed) {
foreach ($fileName in $requiredDesktopScreenshots) {
    $path = Join-Path $screenshotDirectory $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 1024) {
        throw "Required authoritative desktop screenshot is missing or invalid: $path"
    }
}
$firefoxEvidencePath = Join-Path $screenshotDirectory "firefox-e2e.json"
if (-not (Test-Path -LiteralPath $firefoxEvidencePath -PathType Leaf)) {
    throw "Required Firefox E2E metadata is missing: $firefoxEvidencePath"
}
$firefoxEvidence = Get-Content -LiteralPath $firefoxEvidencePath -Raw | ConvertFrom-Json
if ($firefoxEvidence.passed -ne $true -or
    $firefoxEvidence.schemaVersion -ne 4 -or
    $firefoxEvidence.jumpStatus -ne "Success" -or
    $firefoxEvidence.markerVisibleAfterJump -ne $true -or
    $firefoxEvidence.fileOpened -ne $false -or
    $firefoxEvidence.dialogClosedWithoutOpening -ne $true -or
    $firefoxEvidence.interactionPath -ne "NativeHookHostDllDirectCom" -or
    $firefoxEvidence.transport -ne "NativeHook" -or
    $firefoxEvidence.directNavigation -ne "IFileDialog.SetFolder" -or
    $firefoxEvidence.hookInstalledBeforeDialog -ne $true -or
    $firefoxEvidence.hookLoadedBeforeShow -ne $true -or
    $firefoxEvidence.firefoxFileDialogUtility -ne $true -or
    $firefoxEvidence.preloadConfirmedBeforeDialog -ne $true -or
    $firefoxEvidence.fallbackUsed -ne $false -or
    $firefoxEvidence.automationUsed -ne $false -or
    $firefoxEvidence.quickSwitchVisibleAfterJump -ne $true -or
    $firefoxEvidence.quickSwitchUncloakedAfterJump -ne $true -or
    $firefoxEvidence.quickSwitchUnoccludedAfterJump -ne $true -or
    $firefoxEvidence.fileNameBeforeJump -ne "" -or
    $firefoxEvidence.fileNameAfterJump -ne "" -or
    $firefoxEvidence.fileNameInputUnchanged -ne $true -or
    $firefoxEvidence.dialogTextInputObserved -ne $false -or
    $firefoxEvidence.dialog.className -ne "#32770" -or
    $firefoxEvidence.dialog.processName -ne "firefox" -or
    $firefoxEvidence.dialog.processId -le 0 -or
    $firefoxEvidence.dialog.bounds.width -le 0 -or
    $firefoxEvidence.dialog.bounds.height -le 0 -or
    [string]::IsNullOrWhiteSpace($firefoxEvidence.targetFolder) -or
    [string]::IsNullOrWhiteSpace($firefoxEvidence.selectedFileName)) {
    throw "Firefox E2E metadata does not prove a preloaded native IFileDialog::SetFolder jump."
}
$expectedHookHost = Join-Path $PackageDirectory "hooks\x64\ListaryOpen.HookHost.exe"
$expectedHookDll = Join-Path $PackageDirectory "hooks\x64\ListaryOpen.Hook.dll"
if (-not [IO.Path]::GetFullPath($firefoxEvidence.hookHostPath).Equals([IO.Path]::GetFullPath($expectedHookHost), [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFullPath($firefoxEvidence.hookDllPath).Equals([IO.Path]::GetFullPath($expectedHookDll), [StringComparison]::OrdinalIgnoreCase) -or
    (Get-FileHash -LiteralPath $expectedHookDll -Algorithm SHA256).Hash -ne $firefoxEvidence.hookDllSha256) {
    throw "Firefox E2E did not use the packaged x64 native hook binaries or the hook DLL hash changed."
}
}
else {
    Write-Warning "Authoritative desktop screenshot and Firefox metadata validation is advisory because desktop integration did not pass in this CI session."
    $desktopVisualMatrix.artifacts = @()
    $requiredDesktopScreenshots = @()
}
$dialogHookSourceRoots = @(
    (Join-Path $repositoryRoot "src\ListaryOpen.Infrastructure\Dialog"),
    (Join-Path $repositoryRoot "src\ListaryOpen.Infrastructure\Hooks"),
    (Join-Path $repositoryRoot "native\ListaryOpen.Hooks")
)
$dialogHookSourceFiles = @(
    Get-ChildItem -LiteralPath $dialogHookSourceRoots -Recurse -File |
        Where-Object Extension -in @(".cs", ".rs", ".cpp", ".c", ".h", ".hpp")
)
$forbiddenDialogFallbackPattern = "SendInput|keybd_event|WM_KEYDOWN|WM_KEYUP|WM_CHAR|WM_SETTEXT|EM_REPLACESEL|ValuePattern|Clipboard|SendKeys|SetWindowText"
$dialogHookSourceViolations = @(
    $dialogHookSourceFiles | Select-String -Pattern $forbiddenDialogFallbackPattern
)

# Managed dialog/Hook jump code has no legitimate reason to synthesize window
# messages. Native HookHost/DLL sources are excluded from this call-name rule
# because they require PostMessage/SendMessage for WM_NULL queue synchronization,
# authenticated WM_COPYDATA transport and the native BFFM selection contract;
# the keyboard-message symbols above remain forbidden across every native source.
$managedDialogJumpSourceRoots = @(
    (Join-Path $repositoryRoot "src\ListaryOpen.Infrastructure\Dialog"),
    (Join-Path $repositoryRoot "src\ListaryOpen.Infrastructure\Hooks")
)
$forbiddenManagedDialogMessageFallbackPattern = "\b(?:Post(?:Thread)?Message(?:A|W)?|SendMessage(?:Timeout)?(?:A|W)?|SendDlgItemMessage(?:A|W)?)\b"
$dialogHookSourceViolations += @(
    Get-ChildItem -LiteralPath $managedDialogJumpSourceRoots -Recurse -File |
        Where-Object Extension -in @(".cs", ".cpp", ".c", ".h", ".hpp") |
        Select-String -Pattern $forbiddenManagedDialogMessageFallbackPattern
)
if ($dialogHookSourceViolations.Count -gt 0) {
    $violationSummary = $dialogHookSourceViolations | ForEach-Object {
        "$($_.Path):$($_.LineNumber):$($_.Line.Trim())"
    }
    throw "Dialog/hook production source contains a forbidden typed-input, UI Automation, clipboard or window-message fallback:`n$($violationSummary -join "`n")"
}
if (Test-Path -LiteralPath (Join-Path $repositoryRoot "src\ListaryOpen.Infrastructure\Dialog\WindowsDialogAutomation.cs")) {
    throw "The removed Windows dialog automation fallback has been reintroduced."
}
$unsafeForegroundAssertions = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tests\ListaryOpen.IntegrationTests") -Filter "*.cs" -File |
        Select-String -Pattern "Assert.True\(SetForegroundWindow\("
)
if ($unsafeForegroundAssertions.Count -gt 0) {
    $violationSummary = $unsafeForegroundAssertions | ForEach-Object {
        "$($_.Path):$($_.LineNumber):$($_.Line.Trim())"
    }
    throw "Desktop E2E contains unreliable SetForegroundWindow return-value assertions; use DesktopWindowActivator and verify the actual foreground HWND:`n$($violationSummary -join "`n")"
}
if ($desktopIntegrationPassed) {
    $firefoxScreenshotPath = Join-Path $screenshotDirectory $firefoxEvidence.screenshot
    if (-not (Test-Path -LiteralPath $firefoxScreenshotPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $firefoxScreenshotPath -Algorithm SHA256).Hash -ne $firefoxEvidence.screenshotSha256) {
        throw "Firefox E2E screenshot is missing or does not match its recorded SHA256."
    }
}

function Get-PassedTrxTestNames {
    param([string[]]$Paths)

    foreach ($path in $Paths) {
        [xml]$document = Get-Content -LiteralPath $path -Raw
        foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
            if ([string]::Equals($result.outcome, "Passed", [StringComparison]::OrdinalIgnoreCase)) {
                $result.testName
            }
        }
    }
}

function Get-PassedTrxTestIdentities {
    param([Parameter(Mandatory = $true)][string]$Path)

    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $definitions = @{}
    foreach ($definition in $document.SelectNodes("//*[local-name()='UnitTest']")) {
        $method = $definition.SelectSingleNode("./*[local-name()='TestMethod']")
        if ($null -ne $method) {
            $definitions[[string]$definition.id] = [pscustomobject]@{
                Class = [string]$method.className
                Method = [string]$method.name
            }
        }
    }

    foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
        if ([string]::Equals($result.outcome, "Passed", [StringComparison]::OrdinalIgnoreCase) -and
            $definitions.ContainsKey([string]$result.testId)) {
            $identity = $definitions[[string]$result.testId]
            [pscustomobject]@{
                Class = $identity.Class
                Method = $identity.Method
                DisplayName = [string]$result.testName
            }
        }
    }
}

$dotnetTestNames = @(Get-PassedTrxTestNames -Paths @(
    (Join-Path $ResultsDirectory "core.trx"),
    (Join-Path $ResultsDirectory "infrastructure.trx")))
$dotnetTestIdentities = @(
    Get-PassedTrxTestIdentities -Path (Join-Path $ResultsDirectory "core.trx")
    Get-PassedTrxTestIdentities -Path (Join-Path $ResultsDirectory "infrastructure.trx")
)
$desktopTestNames = @(Get-PassedTrxTestNames -Paths @((Join-Path $ResultsDirectory "desktop.trx")))
$desktopTestIdentities = @(Get-PassedTrxTestIdentities -Path (Join-Path $ResultsDirectory "desktop.trx"))
$elevatedDesktopTestNames = @(Get-PassedTrxTestNames -Paths @((Join-Path $ResultsDirectory "elevated-desktop.trx")))
$elevatedDesktopTestIdentities = @(Get-PassedTrxTestIdentities -Path (Join-Path $ResultsDirectory "elevated-desktop.trx"))
$elevatedIndexerTestNames = @(Get-PassedTrxTestNames -Paths @((Join-Path $ResultsDirectory "elevated-indexer.trx")))
$elevatedIndexerTestIdentities = @(Get-PassedTrxTestIdentities -Path (Join-Path $ResultsDirectory "elevated-indexer.trx"))
$packagedBlackboxTestNames = @(Get-PassedTrxTestNames -Paths @((Join-Path $ResultsDirectory "packaged-blackbox.trx")))
$packagedBlackboxTestIdentities = @(Get-PassedTrxTestIdentities -Path (Join-Path $ResultsDirectory "packaged-blackbox.trx"))
$integrationTestIdentitiesBySuite = @{
    desktop = $desktopTestIdentities
    elevatedDesktop = $elevatedDesktopTestIdentities
    elevatedIndexer = $elevatedIndexerTestIdentities
    packagedBlackbox = $packagedBlackboxTestIdentities
}
foreach ($artifact in $desktopVisualMatrix.artifacts) {
    if (-not $integrationTestIdentitiesBySuite.ContainsKey([string]$artifact.suite)) {
        throw "Desktop screenshot '$($artifact.file)' references unknown test suite '$($artifact.suite)'."
    }
    $producerIdentities = $integrationTestIdentitiesBySuite[[string]$artifact.suite]
    $passedProducerInstances = @($producerIdentities | Where-Object {
        [string]::Equals($_.Class, $artifact.testClass, [StringComparison]::Ordinal) -and
        [string]::Equals($_.Method, $artifact.testMethod, [StringComparison]::Ordinal)
    }).Count
    if ($passedProducerInstances -lt $artifact.minimumPassedInstances) {
        throw "Desktop screenshot '$($artifact.file)' lacks its exact passed producer '$($artifact.testClass).$($artifact.testMethod)' ($passedProducerInstances/$($artifact.minimumPassedInstances))."
    }
    if (-not [string]::IsNullOrWhiteSpace($artifact.metadataFile)) {
        $metadataPath = Join-Path $screenshotDirectory $artifact.metadataFile
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or
            (Get-Item -LiteralPath $metadataPath).Length -le 128) {
            throw "Desktop screenshot '$($artifact.file)' lacks authoritative metadata: $metadataPath"
        }
    }
}
$explorerEvidencePath = Join-Path $screenshotDirectory "27-real-explorer-selection.json"
$explorerEvidence = Get-Content -LiteralPath $explorerEvidencePath -Raw | ConvertFrom-Json
$explorerScreenshotPath = Join-Path $screenshotDirectory "27-real-explorer-selection.png"
if ($explorerEvidence.scenario -ne "real-system-explorer-selection" -or
    $explorerEvidence.realSystemExplorer -ne $true -or
    $explorerEvidence.processName -ne "explorer" -or
    $explorerEvidence.windowClass -ne "CabinetWClass" -or
    $explorerEvidence.windowHandle -eq 0 -or
    [string]::IsNullOrWhiteSpace($explorerEvidence.folderPath) -or
    [string]::IsNullOrWhiteSpace($explorerEvidence.selectedPath) -or
    $explorerEvidence.uiaOracle.isSelected -ne $true -or
    @($explorerEvidence.shellOracleSelectedPaths | Where-Object { $_ -eq $explorerEvidence.selectedPath }).Count -ne 1 -or
    -not (Test-Path -LiteralPath $explorerScreenshotPath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $explorerScreenshotPath -Algorithm SHA256).Hash -ne $explorerEvidence.screenshotSha256) {
    throw "Real Explorer evidence does not prove selection through independent Shell COM and UI Automation or its screenshot hash changed."
}

$expectedPackagedExePath = [IO.Path]::GetFullPath((Join-Path $PackageDirectory "ListaryOpen.App.exe"))
$expectedPackagedExeSha256 = (Get-FileHash -LiteralPath $expectedPackagedExePath -Algorithm SHA256).Hash
$realTaskManagerEvidencePath = Join-Path $screenshotDirectory "28-real-task-manager-overlay-selection.json"
$realTaskManagerScreenshotPath = Join-Path $screenshotDirectory "28-real-task-manager-overlay-selection.png"
$realTaskManagerEvidence = Get-Content -LiteralPath $realTaskManagerEvidencePath -Raw | ConvertFrom-Json
$realTaskInitial = $realTaskManagerEvidence.transitions[0]
$realTaskPreview = $realTaskManagerEvidence.transitions[1]
$realTaskConfirmed = $realTaskManagerEvidence.transitions[2]
if ($realTaskManagerEvidence.passed -ne $true -or
    $realTaskManagerEvidence.scenario -ne "real-system-task-manager-production-overlay-selection-sync" -or
    $realTaskManagerEvidence.category -ne "ElevatedDesktopIntegration" -or
    $realTaskManagerEvidence.realSystemTaskManager -ne $true -or
    $realTaskManagerEvidence.fakeServiceUsed -ne $false -or
    $realTaskManagerEvidence.skipped -ne $false -or
    $realTaskManagerEvidence.testProcessSecurity.IsElevated -ne $true -or
    $realTaskManagerEvidence.taskManager.processName -ne "Taskmgr" -or
    $realTaskManagerEvidence.taskManager.windowClass -ne "TaskManagerWindow" -or
    @($realTaskManagerEvidence.transitions).Count -ne 3 -or
    $realTaskInitial.overlaySelectedIdentity.runtimeId -ne $realTaskInitial.taskManagerUiaSelectedIdentity.RuntimeId -or
    $realTaskPreview.overlaySelectedIdentity.runtimeId -eq $realTaskInitial.overlaySelectedIdentity.runtimeId -or
    $realTaskPreview.taskManagerUiaSelectedIdentity.RuntimeId -ne $realTaskInitial.taskManagerUiaSelectedIdentity.RuntimeId -or
    $realTaskPreview.hostSelectionChanged -ne $false -or
    $realTaskConfirmed.overlaySelectedIdentity.runtimeId -ne $realTaskPreview.overlaySelectedIdentity.runtimeId -or
    $realTaskConfirmed.overlaySelectedIdentity.runtimeId -ne $realTaskConfirmed.taskManagerUiaSelectedIdentity.RuntimeId -or
    $realTaskConfirmed.hostSelectionChanged -ne $true -or
    (Get-FileHash -LiteralPath $realTaskManagerScreenshotPath -Algorithm SHA256).Hash -ne $realTaskManagerEvidence.screenshotSha256) {
    throw "Elevated real Task Manager evidence does not prove preview-without-host-mutation followed by explicit synchronized confirmation."
}

$expectedPackagedHookDll = [IO.Path]::GetFullPath((Join-Path $PackageDirectory "hooks\x64\ListaryOpen.Hook.dll"))
$expectedPackagedHookRuntime = [IO.Path]::GetFullPath((Join-Path $PackageDirectory "hooks\x64\libunwind.dll"))
$packagedExplorerEvidencePath = Join-Path $screenshotDirectory "28-packaged-real-explorer-overlay.json"
$packagedExplorerScreenshotPath = Join-Path $screenshotDirectory "28-packaged-real-explorer-overlay.png"
$packagedExplorerEvidence = Get-Content -LiteralPath $packagedExplorerEvidencePath -Raw | ConvertFrom-Json
if ($packagedExplorerEvidence.passed -ne $true -or
    $packagedExplorerEvidence.scenario -ne "packaged-elevated-app-real-explorer-type-search" -or
    $packagedExplorerEvidence.elevatedTestProcess -ne $true -or
    $packagedExplorerEvidence.interactionPath -ne "ExplorerItemsView-WindowsSendInput-GlobalLowLevelHook" -or
    -not [IO.Path]::GetFullPath($packagedExplorerEvidence.packageExePath).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedExplorerEvidence.packageExeSha256 -ne $expectedPackagedExeSha256 -or
    $packagedExplorerEvidence.explorer.processName -ne "explorer" -or
    $packagedExplorerEvidence.explorer.className -ne "CabinetWClass" -or
    $packagedExplorerEvidence.queryOracle.value -ne $packagedExplorerEvidence.query -or
    $packagedExplorerEvidence.resultOracle.isSelected -ne $true -or
    $packagedExplorerEvidence.nativeUnloadOracle.targetProcess -ne "explorer" -or
    $packagedExplorerEvidence.nativeUnloadOracle.targetProcessId -le 0 -or
    -not [IO.Path]::GetFullPath($packagedExplorerEvidence.nativeUnloadOracle.hookDllPath).Equals($expectedPackagedHookDll, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFullPath($packagedExplorerEvidence.nativeUnloadOracle.hookRuntimePath).Equals($expectedPackagedHookRuntime, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedExplorerEvidence.nativeUnloadOracle.authenticatedShutdownCompleted -ne $true -or
    $packagedExplorerEvidence.nativeUnloadOracle.unloadedAfterAuthenticatedShutdown -ne $true -or
    $packagedExplorerEvidence.nativeUnloadOracle.waitTimeoutMs -ne 10000 -or
    @($packagedExplorerEvidence.shellOracleSelectedPaths | Where-Object { $_ -eq $packagedExplorerEvidence.selectedPath }).Count -ne 1 -or
    (Get-FileHash -LiteralPath $packagedExplorerScreenshotPath -Algorithm SHA256).Hash -ne $packagedExplorerEvidence.screenshotSha256) {
    throw "Packaged Explorer evidence does not prove real injected-input routing and synchronized Shell selection."
}

$packagedTaskManagerEvidencePath = Join-Path $screenshotDirectory "29-packaged-real-task-manager-overlay.json"
$packagedTaskManagerScreenshotPath = Join-Path $screenshotDirectory "29-packaged-real-task-manager-overlay.png"
$packagedTaskManagerEvidence = Get-Content -LiteralPath $packagedTaskManagerEvidencePath -Raw | ConvertFrom-Json
$initialTaskTransition = $packagedTaskManagerEvidence.transitions[0]
$previewTaskTransition = $packagedTaskManagerEvidence.transitions[1]
$confirmedTaskTransition = $packagedTaskManagerEvidence.transitions[2]
if ($packagedTaskManagerEvidence.passed -ne $true -or
    $packagedTaskManagerEvidence.scenario -ne "packaged-elevated-app-real-task-manager-type-search" -or
    $packagedTaskManagerEvidence.elevatedTestProcess -ne $true -or
    $packagedTaskManagerEvidence.interactionPath -ne "TaskManagerContentRow-WindowsSendInput-GlobalLowLevelHook" -or
    -not [IO.Path]::GetFullPath($packagedTaskManagerEvidence.packageExePath).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedTaskManagerEvidence.packageExeSha256 -ne $expectedPackagedExeSha256 -or
    $packagedTaskManagerEvidence.taskManager.realSystemTaskManager -ne $true -or
    $packagedTaskManagerEvidence.taskManager.processName -ne "Taskmgr" -or
    $packagedTaskManagerEvidence.taskManager.className -ne "TaskManagerWindow" -or
    $packagedTaskManagerEvidence.queryOracle.value -ne $packagedTaskManagerEvidence.query -or
    $packagedTaskManagerEvidence.nativeUnloadOracle.targetProcess -ne "Taskmgr" -or
    $packagedTaskManagerEvidence.nativeUnloadOracle.targetProcessId -le 0 -or
    -not [IO.Path]::GetFullPath($packagedTaskManagerEvidence.nativeUnloadOracle.hookDllPath).Equals($expectedPackagedHookDll, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFullPath($packagedTaskManagerEvidence.nativeUnloadOracle.hookRuntimePath).Equals($expectedPackagedHookRuntime, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedTaskManagerEvidence.nativeUnloadOracle.authenticatedShutdownCompleted -ne $true -or
    $packagedTaskManagerEvidence.nativeUnloadOracle.unloadedAfterAuthenticatedShutdown -ne $true -or
    $packagedTaskManagerEvidence.nativeUnloadOracle.waitTimeoutMs -ne 10000 -or
    @($packagedTaskManagerEvidence.transitions).Count -ne 3 -or
    $initialTaskTransition.overlaySelectedIdentity.RuntimeId -ne $initialTaskTransition.taskManagerSelectedIdentity.RuntimeId -or
    $previewTaskTransition.overlaySelectedIdentity.RuntimeId -eq $initialTaskTransition.overlaySelectedIdentity.RuntimeId -or
    $previewTaskTransition.taskManagerSelectedIdentity.RuntimeId -ne $initialTaskTransition.taskManagerSelectedIdentity.RuntimeId -or
    $confirmedTaskTransition.overlaySelectedIdentity.RuntimeId -ne $previewTaskTransition.overlaySelectedIdentity.RuntimeId -or
    $confirmedTaskTransition.overlaySelectedIdentity.RuntimeId -ne $confirmedTaskTransition.taskManagerSelectedIdentity.RuntimeId -or
    $packagedTaskManagerEvidence.enterConfirmedAndDismissedOverlay -ne $true -or
    $packagedTaskManagerEvidence.fakeServiceUsed -ne $false -or
    (Get-FileHash -LiteralPath $packagedTaskManagerScreenshotPath -Algorithm SHA256).Hash -ne $packagedTaskManagerEvidence.screenshotSha256) {
    throw "Packaged Task Manager evidence does not prove injected Down preview without host mutation followed by injected Enter confirmation."
}

$packagedLifecycleEvidencePath = Join-Path $screenshotDirectory "29-packaged-app-lifecycle.json"
$packagedLifecycleEvidence = Get-Content -LiteralPath $packagedLifecycleEvidencePath -Raw | ConvertFrom-Json
if ($packagedLifecycleEvidence.passed -ne $true -or
    $packagedLifecycleEvidence.scenario -ne "packaged-elevated-app-background-lifecycle-and-single-instance" -or
    $packagedLifecycleEvidence.elevatedTestProcess -ne $true -or
    -not [IO.Path]::GetFullPath($packagedLifecycleEvidence.packageExePath).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedLifecycleEvidence.packageExeSha256 -ne $expectedPackagedExeSha256 -or
    $packagedLifecycleEvidence.backgroundHealthOracle.readyBeforeDuplicate -ne $true -or
    $packagedLifecycleEvidence.backgroundHealthOracle.readyAfterDuplicate -ne $true -or
    @($packagedLifecycleEvidence.backgroundHealthOracle.firstVisibleTopLevelWindows).Count -ne 0 -or
    $packagedLifecycleEvidence.backgroundHealthOracle.registeredWindowsShellTrayIcon.Handle -eq 0 -or
    [string]::IsNullOrWhiteSpace($packagedLifecycleEvidence.backgroundHealthOracle.registeredWindowsShellTrayIcon.OwnerWindowClass) -or
    $packagedLifecycleEvidence.duplicateLaunchOracle.exitCode -ne 0 -or
    @($packagedLifecycleEvidence.duplicateLaunchOracle.visibleOrModalTopLevelWindows).Count -ne 0 -or
    $packagedLifecycleEvidence.dataDirectoryOracle.initiallyAbsent -ne $true -or
    $packagedLifecycleEvidence.dataDirectoryOracle.absentAfterCleanup -ne $true -or
    $packagedLifecycleEvidence.executableHashUnchangedAfterShutdown -ne $true -or
    $packagedLifecycleEvidence.normalShutdownExitCode -ne 0) {
    throw "Packaged lifecycle evidence does not prove a healthy background instance and silent duplicate exit."
}

$packagedFirefoxEvidencePath = Join-Path $screenshotDirectory "30-packaged-firefox-direct-hotkey.json"
$packagedFirefoxScreenshotPath = Join-Path $screenshotDirectory "30-packaged-firefox-direct-hotkey.png"
$packagedFirefoxEvidence = Get-Content -LiteralPath $packagedFirefoxEvidencePath -Raw | ConvertFrom-Json
$expectedPackagedHookDllSha256 = (Get-FileHash -LiteralPath $expectedPackagedHookDll -Algorithm SHA256).Hash
if ($packagedFirefoxEvidence.passed -ne $true -or
    $packagedFirefoxEvidence.scenario -ne "published-elevated-app-real-firefox-native-hook-ctrl-g" -or
    -not [IO.Path]::GetFullPath($packagedFirefoxEvidence.package.executable).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedFirefoxEvidence.package.executableSha256 -ne $expectedPackagedExeSha256 -or
    -not [IO.Path]::GetFullPath($packagedFirefoxEvidence.package.nativeHookDll).Equals($expectedPackagedHookDll, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedFirefoxEvidence.package.nativeHookDllSha256 -ne $expectedPackagedHookDllSha256 -or
    $packagedFirefoxEvidence.realWindows.dialogClass -ne "#32770" -or
    $packagedFirefoxEvidence.realWindows.dialogProcessId -le 0 -or
    $packagedFirefoxEvidence.trigger.input -ne "Ctrl+G" -or
    @($packagedFirefoxEvidence.trigger.e2eControlCommands | Where-Object { $_ -eq "DialogPrecaptureProof" }).Count -ne 1 -or
    $packagedFirefoxEvidence.trigger.testConstructedQuickSwitch -ne $false -or
    $packagedFirefoxEvidence.trigger.testConstructedDialogBridge -ne $false -or
    $packagedFirefoxEvidence.trigger.testInvokedBridge -ne $false -or
    $packagedFirefoxEvidence.directNavigationOracle.breadcrumbReachedTarget -ne $true -or
    $packagedFirefoxEvidence.directNavigationOracle.markerVisibleAfterHotkey -ne $true -or
    $packagedFirefoxEvidence.directNavigationOracle.nativeHookDllLoadedInDialogProcess -ne $true -or
    $packagedFirefoxEvidence.directNavigationOracle.firefoxFileDialogUtility -ne $true -or
    $packagedFirefoxEvidence.directNavigationOracle.preloadConfirmedBeforeDialog -ne $true -or
    $packagedFirefoxEvidence.directNavigationOracle.fileNameBefore -ne "" -or
    $packagedFirefoxEvidence.directNavigationOracle.fileNameAfter -ne "" -or
    $packagedFirefoxEvidence.directNavigationOracle.quickSwitchQueryBefore -ne "" -or
    $packagedFirefoxEvidence.directNavigationOracle.quickSwitchQueryAfter -ne "o" -or
    $packagedFirefoxEvidence.directNavigationOracle.fileNameInputUnchangedAndEmpty -ne $true -or
    $packagedFirefoxEvidence.directNavigationOracle.quickSwitchQueryUnchangedAndEmpty -ne $false -or
    $packagedFirefoxEvidence.directNavigationOracle.fallback -ne $false -or
    $packagedFirefoxEvidence.nativeUnloadOracle.targetProcess -ne "firefox" -or
    $packagedFirefoxEvidence.nativeUnloadOracle.targetProcessId -le 0 -or
    -not [IO.Path]::GetFullPath($packagedFirefoxEvidence.nativeUnloadOracle.hookDllPath).Equals($expectedPackagedHookDll, [StringComparison]::OrdinalIgnoreCase) -or
    -not [IO.Path]::GetFullPath($packagedFirefoxEvidence.nativeUnloadOracle.hookRuntimePath).Equals($expectedPackagedHookRuntime, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedFirefoxEvidence.nativeUnloadOracle.authenticatedShutdownCompleted -ne $true -or
    $packagedFirefoxEvidence.nativeUnloadOracle.unloadedAfterAuthenticatedShutdown -ne $true -or
    $packagedFirefoxEvidence.nativeUnloadOracle.waitTimeoutMs -ne 10000 -or
    $packagedFirefoxEvidence.selectionOracle.fileOpened -ne $false -or
    $packagedFirefoxEvidence.selectionOracle.dialogClosedWithoutOpening -ne $true -or
    $packagedFirefoxEvidence.visualOracle.quickSwitchVisible -ne $true -or
    $packagedFirefoxEvidence.visualOracle.quickSwitchUncloaked -ne $true -or
    $packagedFirefoxEvidence.visualOracle.quickSwitchUnoccluded -ne $true -or
    (Get-FileHash -LiteralPath $packagedFirefoxScreenshotPath -Algorithm SHA256).Hash -ne $packagedFirefoxEvidence.visualOracle.screenshotSha256) {
    throw "Packaged Firefox evidence does not prove a real Ctrl+G native-hook direct jump with zero path input."
}
if (@($desktopTestIdentities | Where-Object {
            [string]::Equals($_.Class, "ListaryOpen.IntegrationTests.FirefoxQuickSwitchIntegrationTests", [StringComparison]::Ordinal) -and
            [string]::Equals($_.Method, "FirefoxCtrlOFilePickerAttachesQuickSwitchAndJumpsWithoutOpeningFile", [StringComparison]::Ordinal)
        }).Count -ne 1) {
    throw "Firefox E2E metadata exists without exactly one matching passed desktop test."
}

$pluginEvidencePath = Join-Path $screenshotDirectory "31-nonstandard-dialog-plugin-direct.json"
$pluginScreenshotPath = Join-Path $screenshotDirectory "31-nonstandard-dialog-plugin-direct.png"
$pluginEvidence = Get-Content -LiteralPath $pluginEvidencePath -Raw | ConvertFrom-Json
if ($pluginEvidence.passed -ne $true -or
    $pluginEvidence.scenario -ne "compiled-dialog-plugin-real-nonstandard-host-structured-direct-navigation" -or
    $pluginEvidence.pluginId -ne "listaryopen.fixture.custom-browser" -or
    $pluginEvidence.loader -ne "DialogPluginLoader" -or
    $pluginEvidence.host.className -ne "ListaryOpenPluginFixtureBrowser" -or
    $pluginEvidence.host.standardDialog -ne $false -or
    $pluginEvidence.transport -ne "WM_COPYDATA-json-v1-SetFolder" -or
    $pluginEvidence.pathInputSimulation -ne $false -or
    $pluginEvidence.fallbackUsed -ne $false -or
    $pluginEvidence.hostState.Stage -ne "CustomBrowserNavigated" -or
    $pluginEvidence.hostState.SelectedPath -ne $pluginEvidence.targetFolder -or
    $pluginEvidence.visualOracle.breadcrumbName -ne $pluginEvidence.targetFolder -or
    $pluginEvidence.visualOracle.breadcrumbVisible -ne $true -or
    (Get-FileHash -LiteralPath $pluginScreenshotPath -Algorithm SHA256).Hash -ne $pluginEvidence.screenshotSha256) {
    throw "Nonstandard dialog plugin evidence does not prove a compiled, structured direct navigation without path input fallback."
}

$packagedSettingsEvidencePath = Join-Path $screenshotDirectory "30-packaged-settings-persistence.json"
$packagedSettingsEvidence = Get-Content -LiteralPath $packagedSettingsEvidencePath -Raw | ConvertFrom-Json
if ($packagedSettingsEvidence.passed -ne $true -or
    $packagedSettingsEvidence.scenario -ne "packaged-tray-settings-persistence-across-restart" -or
    $packagedSettingsEvidence.elevatedTestProcess -ne $true -or
    -not [IO.Path]::GetFullPath($packagedSettingsEvidence.packageExePath).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedSettingsEvidence.packageExeSha256 -ne $expectedPackagedExeSha256 -or
    $packagedSettingsEvidence.trayOracle.firstInstance.Handle -eq 0 -or
    $packagedSettingsEvidence.trayOracle.restartedInstance.Handle -eq 0 -or
    $packagedSettingsEvidence.persistenceOracle.version -ne 4 -or
    $packagedSettingsEvidence.persistenceOracle.serializedThemeValue -ne 3 -or
    $packagedSettingsEvidence.persistenceOracle.firstSelectedTheme -ne "Geek" -or
    $packagedSettingsEvidence.persistenceOracle.restartedSelectedTheme -ne "Geek" -or
    $packagedSettingsEvidence.persistenceOracle.temporaryFileAbsent -ne $true -or
    $packagedSettingsEvidence.persistenceOracle.dataDirectoryRemovedAfterTest -ne $true -or
    @($packagedSettingsEvidence.screenshots).Count -ne 2) {
    throw "Packaged tray/settings evidence does not prove Shell registration, UI save and restart persistence."
}
foreach ($settingsScreenshot in $packagedSettingsEvidence.screenshots) {
    $settingsScreenshotPath = Join-Path $screenshotDirectory $settingsScreenshot.file
    if (-not (Test-Path -LiteralPath $settingsScreenshotPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $settingsScreenshotPath -Algorithm SHA256).Hash -ne $settingsScreenshot.sha256) {
        throw "Packaged settings screenshot is missing or its recorded SHA256 changed: $($settingsScreenshot.file)"
    }
}

$packagedSearchEvidencePath = Join-Path $screenshotDirectory "31-packaged-global-search-context-menu.json"
$packagedSearchScreenshotPath = Join-Path $screenshotDirectory "31-packaged-global-search-context-menu.png"
$packagedSearchEvidence = Get-Content -LiteralPath $packagedSearchEvidencePath -Raw | ConvertFrom-Json
if ($packagedSearchEvidence.passed -ne $true -or
    $packagedSearchEvidence.scenario -ne "packaged-elevated-global-hotkey-search-preview-menu-and-quick-launch" -or
    -not [IO.Path]::GetFullPath($packagedSearchEvidence.package.executable).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedSearchEvidence.package.executableSha256 -ne $expectedPackagedExeSha256 -or
    $packagedSearchEvidence.input.transport -ne "WindowsSendInput" -or
    $packagedSearchEvidence.input.globalHotkey -ne "Ctrl+Space" -or
    @($packagedSearchEvidence.preseed.indexedResults).Count -ne 2 -or
    $packagedSearchEvidence.searchOracle.selectionChanged -ne $true -or
    [string]::IsNullOrWhiteSpace($packagedSearchEvidence.searchOracle.previewExactText) -or
    @($packagedSearchEvidence.searchOracle.contextMenuItems).Count -ne 3 -or
    @($packagedSearchEvidence.searchOracle.contextMenuItems | Where-Object { $_ -eq "Open / switch" }).Count -ne 1 -or
    $packagedSearchEvidence.searchOracle.firstEscapeClosedMenuOnly -ne $true -or
    $packagedSearchEvidence.searchOracle.secondEscapeDismissedSearch -ne $true -or
    $packagedSearchEvidence.quickLaunchOracle.productionExecutor -ne $true -or
    $packagedSearchEvidence.quickLaunchOracle.markerExact -ne $true -or
    $packagedSearchEvidence.quickLaunchOracle.searchDismissedAfterLaunch -ne $true -or
    (Get-FileHash -LiteralPath $packagedSearchScreenshotPath -Algorithm SHA256).Hash -ne $packagedSearchEvidence.visualOracle.screenshotSha256) {
    throw "Packaged global-search evidence does not prove hotkey, real results, preview, menu/Escape and Quick Launch behavior."
}

$packagedPluginEvidencePath = Join-Path $screenshotDirectory "32-packaged-nonstandard-plugin-direct.json"
$packagedPluginScreenshotPath = Join-Path $screenshotDirectory "32-packaged-nonstandard-plugin-direct.png"
$packagedPluginEvidence = Get-Content -LiteralPath $packagedPluginEvidencePath -Raw | ConvertFrom-Json
if ($packagedPluginEvidence.passed -ne $true -or
    $packagedPluginEvidence.scenario -ne "published-app-real-ctrl-g-compiled-plugin-nonstandard-direct-navigation" -or
    -not [IO.Path]::GetFullPath($packagedPluginEvidence.package.executable).Equals($expectedPackagedExePath, [StringComparison]::OrdinalIgnoreCase) -or
    $packagedPluginEvidence.package.executableSha256 -ne $expectedPackagedExeSha256 -or
    $packagedPluginEvidence.plugin.id -ne "listaryopen.fixture.custom-browser" -or
    $packagedPluginEvidence.plugin.loadedInPublishedApp -ne $true -or
    $packagedPluginEvidence.nonstandardTarget.className -ne "ListaryOpenPluginFixtureBrowser" -or
    $packagedPluginEvidence.nonstandardTarget.standardDialog -ne $false -or
    $packagedPluginEvidence.nonstandardTarget.initialFolder -eq $packagedPluginEvidence.nonstandardTarget.targetFolder -or
    $packagedPluginEvidence.nonstandardTarget.hostState.Stage -ne "CustomBrowserNavigated" -or
    $packagedPluginEvidence.nonstandardTarget.hostState.SelectedPath -ne $packagedPluginEvidence.nonstandardTarget.targetFolder -or
    $packagedPluginEvidence.transport -ne "WM_COPYDATA-json-v1-SetFolder" -or
    $packagedPluginEvidence.trigger.input -ne "Ctrl+G" -or
    $packagedPluginEvidence.trigger.e2eControlOnlyAllowedInjectedInput -ne $true -or
    $packagedPluginEvidence.directNavigationOracle.breadcrumbName -ne $packagedPluginEvidence.nonstandardTarget.targetFolder -or
    $packagedPluginEvidence.directNavigationOracle.breadcrumbVisible -ne $true -or
    $packagedPluginEvidence.directNavigationOracle.pathInput -ne $false -or
    $packagedPluginEvidence.directNavigationOracle.fallback -ne $false -or
    $packagedPluginEvidence.visualOracle.quickSwitchVisible -ne $true -or
    -not (Test-Path -LiteralPath $packagedPluginScreenshotPath -PathType Leaf) -or
    (Get-FileHash -LiteralPath $packagedPluginScreenshotPath -Algorithm SHA256).Hash -ne $packagedPluginEvidence.visualOracle.screenshotSha256) {
    throw "Packaged plugin evidence does not prove published-app Ctrl+G direct navigation of a nonstandard window without path input fallback."
}
$nativeEvidence = @(
    Get-Content -LiteralPath (Join-Path $ResultsDirectory "native-x64.log") -Raw
    Get-Content -LiteralPath (Join-Path $ResultsDirectory "native-x86.log") -Raw
) -join "`n"

function Assert-NativeTestLog {
    param([Parameter(Mandatory = $true)][string]$Path)

    $content = Get-Content -LiteralPath $Path -Raw
    $summaries = [Regex]::Matches(
        $content,
        'test result: ok\.\s+(\d+) passed;\s+(\d+) failed;\s+(\d+) ignored;\s+(\d+) measured;\s+(\d+) filtered out;')
    if ($summaries.Count -eq 0) {
        throw "Native test log has no parseable successful test summary: $Path"
    }

    $executed = 0
    foreach ($summary in $summaries) {
        $executed += [int]$summary.Groups[1].Value
        if ([int]$summary.Groups[2].Value -ne 0 -or
            [int]$summary.Groups[3].Value -ne 0 -or
            [int]$summary.Groups[4].Value -ne 0 -or
            [int]$summary.Groups[5].Value -ne 0) {
            throw "Native test log contains failed, ignored, measured or filtered tests: $Path"
        }
    }

    if ($executed -le 0) {
        throw "Native test log executed zero tests: $Path"
    }
}

Assert-NativeTestLog (Join-Path $ResultsDirectory "native-x64.log")
Assert-NativeTestLog (Join-Path $ResultsDirectory "native-x86.log")

$coveragePath = Join-Path $repositoryRoot "tests\automation-coverage.json"
$coverage = Get-Content -LiteralPath $coveragePath -Raw | ConvertFrom-Json
if ($coverage.schemaVersion -ne 1 -or $coverage.featureGroups.Count -eq 0) {
    throw "Automation coverage manifest is empty or uses an unsupported schema."
}

$coverageResults = @()
foreach ($feature in $coverage.featureGroups) {
    if ([string]::IsNullOrWhiteSpace($feature.id) -or
        [string]::IsNullOrWhiteSpace($feature.requirement) -or
        $feature.evidence.Count -eq 0) {
        throw "Coverage feature entries require id, requirement and evidence."
    }

    $evidenceResults = @()
    foreach ($evidence in $feature.evidence) {
        $matched = switch ($evidence.suite) {
            "dotnet" {
                @($dotnetTestIdentities | Where-Object {
                    [string]::Equals($_.Method, $evidence.testNameContains, [StringComparison]::Ordinal)
                }).Count -gt 0
            }
            "desktop" {
                @($desktopTestIdentities | Where-Object {
                    [string]::Equals($_.Method, $evidence.testNameContains, [StringComparison]::Ordinal)
                }).Count -gt 0
            }
            "elevatedDesktop" {
                @($elevatedDesktopTestIdentities | Where-Object {
                    [string]::Equals($_.Method, $evidence.testNameContains, [StringComparison]::Ordinal)
                }).Count -gt 0
            }
            "elevatedIndexer" {
                @($elevatedIndexerTestIdentities | Where-Object {
                    [string]::Equals($_.Method, $evidence.testNameContains, [StringComparison]::Ordinal)
                }).Count -gt 0
            }
            "packagedBlackbox" {
                @($packagedBlackboxTestIdentities | Where-Object {
                    [string]::Equals($_.Method, $evidence.testNameContains, [StringComparison]::Ordinal)
                }).Count -gt 0
            }
            "native" {
                $pattern = "(?m)^test .*" + [Regex]::Escape($evidence.testNameContains) + ".* \.\.\. ok\s*$"
                [Regex]::IsMatch($nativeEvidence, $pattern)
            }
            "package" {
                $artifactPath = Join-Path $PackageDirectory ($evidence.artifact -replace '/', [IO.Path]::DirectorySeparatorChar)
                Test-Path -LiteralPath $artifactPath -PathType Leaf
            }
            default { $false }
        }

        $evidenceResults += [pscustomobject]@{
            Suite = $evidence.suite
            Selector = if ($evidence.screenshot) { $evidence.screenshot } elseif ($evidence.testNameContains) { $evidence.testNameContains } else { $evidence.artifact }
            Passed = [bool]$matched
        }
    }

    $featurePassed = @($evidenceResults | Where-Object { -not $_.Passed }).Count -eq 0
    $coverageResults += [pscustomobject]@{
        Id = $feature.id
        Requirement = $feature.requirement
        Passed = $featurePassed
        Evidence = $evidenceResults
    }
}

$coverageResults += [pscustomobject]@{
    Id = "global-native-module-unload"
    Requirement = "Every exact packaged x64/x86 Hook.dll and libunwind.dll image is absent from the current session for two consecutive scans, with no baseline enumeration regression and an independent Restart Manager oracle."
    Passed = $globalNativeModuleUnloadAudit.Passed
    Evidence = @(
        [pscustomobject]@{
            Suite = "runner"
            Selector = [IO.Path]::GetFileName($nativeModuleAuditBaselinePath)
            Passed = Test-Path -LiteralPath $nativeModuleAuditBaselinePath -PathType Leaf
        }
        [pscustomobject]@{
            Suite = "runner"
            Selector = [IO.Path]::GetFileName($nativeModuleUnloadAuditPath)
            Passed = $globalNativeModuleUnloadAudit.Passed -and
                (Test-Path -LiteralPath $nativeModuleUnloadAuditPath -PathType Leaf)
        }
    )
}

$missingCoverage = @($coverageResults | Where-Object { -not $_.Passed })
if ($missingCoverage.Count -gt 0) {
    $details = $missingCoverage | ForEach-Object {
        $missing = $_.Evidence | Where-Object { -not $_.Passed } | ForEach-Object { "$($_.Suite):$($_.Selector)" }
        "$($_.Id) => $($missing -join ', ')"
    }
    throw "Automation coverage evidence is missing:`n$($details -join "`n")"
}

Compress-PackageArchive `
    -PackagePath $PackageDirectory `
    -DataDirectory $packageDataDirectory `
    -DestinationPath $zipPath

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($relativePath in $requiredArtifacts) {
        $entryName = $relativePath -replace '\\', '/'
        $entry = $archive.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
        if ($null -eq $entry -or $entry.Length -le 0) {
            throw "Required ZIP entry is missing or empty: $entryName"
        }
    }

    $publishedFiles = @(Get-PackageFilesForArchive -PackagePath $PackageDirectory -DataDirectory $packageDataDirectory | ForEach-Object {
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($PackageDirectory, $_.FullName).Replace('\', '/')
            FullName = $_.FullName
            Length = $_.Length
        }
    })
    $zipFiles = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
    $duplicateZipEntries = @($zipFiles | Group-Object FullName | Where-Object Count -ne 1)
    if ($duplicateZipEntries.Count -gt 0) {
        throw "ZIP contains duplicate file entries: $($duplicateZipEntries.Name -join ', ')"
    }

    $entryDifferences = @(Compare-Object -ReferenceObject @($publishedFiles.Path | Sort-Object) -DifferenceObject @($zipFiles.FullName | Sort-Object))
    if ($entryDifferences.Count -gt 0) {
        throw "ZIP file set does not exactly match the published package: $($entryDifferences | ConvertTo-Json -Compress)"
    }

    foreach ($publishedFile in $publishedFiles) {
        $entry = $zipFiles | Where-Object FullName -eq $publishedFile.Path | Select-Object -First 1
        if ($entry.Length -ne $publishedFile.Length) {
            throw "ZIP entry size differs from the published file: $($publishedFile.Path)"
        }

        $sha256 = [Security.Cryptography.SHA256]::Create()
        $stream = $entry.Open()
        try {
            $zipHash = [Convert]::ToHexString($sha256.ComputeHash($stream))
        }
        finally {
            $stream.Dispose()
            $sha256.Dispose()
        }
        $publishedHash = (Get-FileHash -LiteralPath $publishedFile.FullName -Algorithm SHA256).Hash
        if ($zipHash -ne $publishedHash) {
            throw "ZIP entry content differs from the published file: $($publishedFile.Path)"
        }
    }
}
finally {
    $archive.Dispose()
}

function Get-TrxCounters {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required TRX file is missing: $Path"
    }
    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
    $summary = $document.SelectSingleNode("//*[local-name()='ResultSummary']")
    if ($null -eq $counters -or $null -eq $summary -or
        -not [string]::Equals($summary.outcome, "Completed", [StringComparison]::OrdinalIgnoreCase)) {
        throw "TRX is missing completed result counters: $Path"
    }
    if ([int]$counters.total -le 0) {
        throw "TRX executed zero tests: $Path"
    }
    [pscustomobject]@{
        Total = [int]$counters.total
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        Skipped = [int]$counters.notExecuted
    }
}

$testCounters = [ordered]@{
    Core = Get-TrxCounters (Join-Path $ResultsDirectory "core.trx")
    Infrastructure = Get-TrxCounters (Join-Path $ResultsDirectory "infrastructure.trx")
    Desktop = Get-TrxCounters (Join-Path $ResultsDirectory "desktop.trx")
    ElevatedDesktop = Get-TrxCounters (Join-Path $ResultsDirectory "elevated-desktop.trx")
    ElevatedIndexer = Get-TrxCounters (Join-Path $ResultsDirectory "elevated-indexer.trx")
    PackagedBlackbox = Get-TrxCounters (Join-Path $ResultsDirectory "packaged-blackbox.trx")
}
foreach ($suite in $testCounters.GetEnumerator()) {
    if ($suite.Value.Total -ne $suite.Value.Passed -or $suite.Value.Failed -ne 0 -or $suite.Value.Skipped -ne 0) {
        throw "Test suite '$($suite.Key)' was not entirely passed: $($suite.Value | ConvertTo-Json -Compress)"
    }
}

$screenshotFiles = @(Get-ChildItem -LiteralPath $screenshotDirectory -Filter "*.png" -File | Sort-Object Name | ForEach-Object {
    if ($_.Length -le 1024) {
        throw "Acceptance screenshot is unexpectedly small: $($_.FullName)"
    }

    [pscustomobject]@{
        Path = $_.Name
        Size = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$expectedScreenshotNames = @($requiredDesktopScreenshots | Sort-Object -Unique)
$screenshotDifferences = @(Compare-Object -ReferenceObject $expectedScreenshotNames -DifferenceObject @($screenshotFiles.Path | Sort-Object))
if ($expectedScreenshotNames.Count -ne $desktopVisualMatrix.artifacts.Count -or
    $screenshotFiles.Count -ne $expectedScreenshotNames.Count -or
    $screenshotDifferences.Count -gt 0) {
    throw "Acceptance evidence must exactly match the repository-owned visual matrices: $($screenshotDifferences | ConvertTo-Json -Compress)"
}

$packageFiles = foreach ($relativePath in $requiredArtifacts) {
    $fullPath = Join-Path $PackageDirectory ($relativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
    $item = Get-Item -LiteralPath $fullPath
    [pscustomobject]@{
        Path = $relativePath
        Size = $item.Length
        Sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    }
}

$report = [pscustomobject]@{
    GeneratedAtUtc = [DateTimeOffset]::UtcNow
    Repository = $repositoryRoot
    Tests = [pscustomobject]@{
        Core = $testCounters.Core
        Infrastructure = $testCounters.Infrastructure
        Desktop = $testCounters.Desktop
        ElevatedDesktop = $testCounters.ElevatedDesktop
        ElevatedIndexer = $testCounters.ElevatedIndexer
        PackagedBlackbox = $testCounters.PackagedBlackbox
        NativeX64 = "Passed"
        NativeX86 = "Passed"
    }
    Coverage = $coverageResults
    DesktopVisualMatrix = $desktopVisualMatrix
    NativeUnloadEvidence = [pscustomobject]@{
        GlobalProcessAudit = $globalNativeModuleUnloadAudit
        Explorer = $packagedExplorerEvidence.nativeUnloadOracle
        TaskManager = $packagedTaskManagerEvidence.nativeUnloadOracle
        Firefox = $packagedFirefoxEvidence.nativeUnloadOracle
    }
    ScreenshotFiles = $screenshotFiles
    PackageFiles = $packageFiles
    Zip = [pscustomobject]@{
        Path = $zipPath
        Size = (Get-Item -LiteralPath $zipPath).Length
        Sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    }
}

$jsonReportPath = Join-Path $ResultsDirectory "acceptance-report.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonReportPath -Encoding utf8
$markdownReportPath = Join-Path $ResultsDirectory "acceptance-report.md"
$markdown = @(
    "# ListaryOpen automated acceptance",
    "",
    "- Core tests: $($report.Tests.Core.Passed)/$($report.Tests.Core.Total)",
    "- Infrastructure/WPF tests: $($report.Tests.Infrastructure.Passed)/$($report.Tests.Infrastructure.Total)",
    "- Desktop evidence: $($report.ScreenshotFiles.Count) screenshots",
    "- Real desktop tests: $($report.Tests.Desktop.Passed)/$($report.Tests.Desktop.Total)",
    "- Elevated desktop tests: $($report.Tests.ElevatedDesktop.Passed)/$($report.Tests.ElevatedDesktop.Total)",
    "- Elevated indexer tests: $($report.Tests.ElevatedIndexer.Passed)/$($report.Tests.ElevatedIndexer.Total)",
    "- Packaged black-box tests: $($report.Tests.PackagedBlackbox.Passed)/$($report.Tests.PackagedBlackbox.Total)",
    "- Native hook suites: x64 passed; x86 passed",
    "- Native unload after authenticated shutdown: Explorer passed; Task Manager passed; Firefox passed",
    "- Global package-module unload audit: two consecutive process scans and Restart Manager oracle passed",
    "- Coverage groups: $(@($coverageResults | Where-Object Passed).Count)/$($coverageResults.Count)",
    "- Package: $zipPath",
    "- Package SHA256: $($report.Zip.Sha256)",
    "",
    "| Feature | Result |",
    "| --- | --- |"
)
$markdown += $coverageResults | ForEach-Object { "| $($_.Id) | $(if ($_.Passed) { 'Passed' } else { 'Failed' }) |" }
$markdown | Set-Content -LiteralPath $markdownReportPath -Encoding utf8

Write-Host "Automated acceptance passed."
Write-Host "Report: $jsonReportPath"
Write-Host "Package: $zipPath"
Write-Host "SHA256: $($report.Zip.Sha256)"
