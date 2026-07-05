# Indexing Performance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move all index artifacts under the program directory, make startup quiet, reduce indexed noise, and stream large NTFS helper output with bounded memory.

**Architecture:** Add explicit app data paths, shared directory exclusion rules, streaming elevated helper import, and a directory-first NTFS projection path. Keep the existing provider/coordinator shape and improve the internals behind those boundaries.

**Tech Stack:** .NET 8, WPF, Microsoft.Data.Sqlite, Windows NTFS USN APIs, xUnit.

---

## File Structure

- Create `src/ListaryOpen.Infrastructure/AppData/AppDataPaths.cs`
  - Resolves `<program-directory>\data`, `<program-directory>\data\index.db`, and `<program-directory>\data\tmp`.
- Create `src/ListaryOpen.Infrastructure/Indexing/IndexExclusionRules.cs`
  - Owns default excluded directory names and directory skip decisions.
- Modify `src/ListaryOpen.App/App.xaml.cs`
  - Uses `AppDataPaths`.
  - Passes indexer temp directory into `ElevatedIndexerClient`.
  - Does not show Settings on ordinary startup.
- Modify `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`
  - Skips excluded directory subtrees.
- Modify `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`
  - Creates JSONL/error files under configured program `data\tmp`.
  - Streams JSONL output line by line.
- Modify `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerProcessStartInfoFactory.cs`
  - Passes output/error paths as before; behavior remains compatible.
- Modify `src/ListaryOpen.Indexer.Elevated/ElevatedIndexerOutputPathValidator.cs`
  - Validates output paths against the supplied program temp directory.
- Modify `src/ListaryOpen.Indexer.Elevated/Program.cs`
  - Accepts `scan-to-file <root> <records-path> <error-path> <allowed-temp-directory>`.
- Modify `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`
  - Uses a directory-first, file-streaming enumeration path.
- Modify `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnRecordProjector.cs`
  - Supports directory map projection and exclusion-aware streaming.
- Add/update tests under `tests/ListaryOpen.Infrastructure.Tests`.

## Task 1: Program Data Paths

**Files:**
- Create: `src/ListaryOpen.Infrastructure/AppData/AppDataPaths.cs`
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppDataPathsTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/ListaryOpen.Infrastructure.Tests/App/AppDataPathsTests.cs`:

```csharp
using ListaryOpen.Infrastructure.AppData;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppDataPathsTests
{
    [Fact]
    public void CreateUnderProgramDirectoryUsesDataSubdirectories()
    {
        var programDirectory = Path.Combine(Path.GetTempPath(), "listary-open-program-" + Guid.NewGuid());

        var paths = AppDataPaths.CreateUnderProgramDirectory(programDirectory);

        Assert.Equal(Path.Combine(programDirectory, "data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(programDirectory, "data", "index.db"), paths.IndexDatabasePath);
        Assert.Equal(Path.Combine(programDirectory, "data", "tmp"), paths.IndexerTempDirectory);
    }

    [Fact]
    public void EnsureDirectoriesCreatesDataAndTmp()
    {
        var programDirectory = Path.Combine(Path.GetTempPath(), "listary-open-program-" + Guid.NewGuid());
        var paths = AppDataPaths.CreateUnderProgramDirectory(programDirectory);

        try
        {
            paths.EnsureDirectories();

            Assert.True(Directory.Exists(paths.DataDirectory));
            Assert.True(Directory.Exists(paths.IndexerTempDirectory));
        }
        finally
        {
            if (Directory.Exists(programDirectory))
            {
                Directory.Delete(programDirectory, recursive: true);
            }
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AppDataPathsTests
```

Expected: compile fails because `ListaryOpen.Infrastructure.AppData.AppDataPaths` does not exist.

- [ ] **Step 3: Implement `AppDataPaths`**

Create `src/ListaryOpen.Infrastructure/AppData/AppDataPaths.cs`:

```csharp
namespace ListaryOpen.Infrastructure.AppData;

public sealed record AppDataPaths(
    string ProgramDirectory,
    string DataDirectory,
    string IndexDatabasePath,
    string IndexerTempDirectory)
{
    public static AppDataPaths CreateDefault()
    {
        return CreateUnderProgramDirectory(AppContext.BaseDirectory);
    }

    public static AppDataPaths CreateUnderProgramDirectory(string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);

        var fullProgramDirectory = Path.GetFullPath(programDirectory);
        var dataDirectory = Path.Combine(fullProgramDirectory, "data");
        return new AppDataPaths(
            fullProgramDirectory,
            dataDirectory,
            Path.Combine(dataDirectory, "index.db"),
            Path.Combine(dataDirectory, "tmp"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(IndexerTempDirectory);
    }
}
```

- [ ] **Step 4: Use paths in App startup**

In `src/ListaryOpen.App/App.xaml.cs`, replace `CreateIndexDatabasePath()` with `CreateAppDataPaths()` and use:

```csharp
var appDataPaths = CreateAppDataPaths();
appDataPaths.EnsureDirectories();
_searchIndex = await SqliteSearchIndex.OpenAsync(appDataPaths.IndexDatabasePath, CancellationToken.None)
    .ConfigureAwait(false);
```

Pass `appDataPaths.IndexerTempDirectory` into the `ElevatedIndexerClient` constructor introduced in Task 4.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AppDataPathsTests
```

Expected: PASS.

## Task 2: Quiet Startup

**Files:**
- Modify: `src/ListaryOpen.App/App.xaml.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/App/AppStartupWindowTests.cs`

- [ ] **Step 1: Write failing test for startup display decision**

Create `tests/ListaryOpen.Infrastructure.Tests/App/AppStartupWindowTests.cs`:

```csharp
using WpfApp = ListaryOpen.App.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class AppStartupWindowTests
{
    [Fact]
    public void ShouldShowSettingsOnStartupReturnsFalseForNormalStartup()
    {
        Assert.False(WpfApp.ShouldShowSettingsOnStartup(hasBlockingStartupMessage: false));
    }

    [Fact]
    public void ShouldShowSettingsOnStartupReturnsTrueForBlockingStartupMessage()
    {
        Assert.True(WpfApp.ShouldShowSettingsOnStartup(hasBlockingStartupMessage: true));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AppStartupWindowTests
```

Expected: compile fails because `ShouldShowSettingsOnStartup` does not exist.

- [ ] **Step 3: Implement quiet startup decision**

Add to `App.xaml.cs`:

```csharp
internal static bool ShouldShowSettingsOnStartup(bool hasBlockingStartupMessage)
{
    return hasBlockingStartupMessage;
}
```

Change startup from unconditional:

```csharp
MainWindow = settingsWindow;
settingsWindow.Show();
```

to:

```csharp
MainWindow = settingsWindow;
if (ShouldShowSettingsOnStartup(hasBlockingStartupMessage: false))
{
    settingsWindow.Show();
}
```

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~AppStartupWindowTests
```

Expected: PASS.

## Task 3: Default Exclusions

**Files:**
- Create: `src/ListaryOpen.Infrastructure/Indexing/IndexExclusionRules.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/FallbackIndexProvider.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/IndexExclusionRulesTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`

- [ ] **Step 1: Write exclusion rule tests**

Create `tests/ListaryOpen.Infrastructure.Tests/Indexing/IndexExclusionRulesTests.cs`:

```csharp
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class IndexExclusionRulesTests
{
    [Theory]
    [InlineData(".git")]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".vs")]
    [InlineData(".idea")]
    [InlineData(".vscode")]
    [InlineData("packages")]
    [InlineData("dist")]
    [InlineData("build")]
    [InlineData(".cache")]
    [InlineData("__pycache__")]
    [InlineData(".pytest_cache")]
    [InlineData(".next")]
    [InlineData(".nuxt")]
    [InlineData("target")]
    public void DefaultRulesExcludeNoisyDirectoryNames(string directoryName)
    {
        Assert.True(IndexExclusionRules.Default.ShouldExcludeDirectoryName(directoryName));
    }

    [Fact]
    public void DefaultRulesDoNotExcludeOrdinaryDirectoryName()
    {
        Assert.False(IndexExclusionRules.Default.ShouldExcludeDirectoryName("Documents"));
    }
}
```

- [ ] **Step 2: Add fallback provider exclusion test**

Append to `tests/ListaryOpen.Infrastructure.Tests/Indexing/FallbackIndexProviderTests.cs`:

```csharp
[Fact]
public async Task ScanAsyncSkipsExcludedDirectorySubtrees()
{
    var root = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
    Directory.CreateDirectory(root);

    try
    {
        var keep = Directory.CreateDirectory(Path.Combine(root, "keep"));
        File.WriteAllText(Path.Combine(keep.FullName, "visible.txt"), "visible");
        var excluded = Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        File.WriteAllText(Path.Combine(excluded.FullName, "hidden.txt"), "hidden");
        var provider = new FallbackIndexProvider();

        var records = new List<ListaryOpen.Core.Indexing.FileRecord>();
        await foreach (var record in provider.ScanAsync(new ListaryOpen.Core.Indexing.IndexRoot(root), CancellationToken.None))
        {
            records.Add(record);
        }

        Assert.Contains(records, record => record.FullPath.EndsWith("visible.txt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(records, record => record.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~IndexExclusionRulesTests|FullyQualifiedName~FallbackIndexProviderTests.ScanAsyncSkipsExcludedDirectorySubtrees"
```

Expected: compile fails because `IndexExclusionRules` does not exist.

- [ ] **Step 4: Implement rules and fallback skip**

Create `src/ListaryOpen.Infrastructure/Indexing/IndexExclusionRules.cs`:

```csharp
namespace ListaryOpen.Infrastructure.Indexing;

public sealed class IndexExclusionRules
{
    private static readonly string[] DefaultDirectoryNames =
    [
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea", ".vscode",
        "packages", "dist", "build", ".cache", "__pycache__", ".pytest_cache", ".next",
        ".nuxt", "target"
    ];

    private readonly HashSet<string> _directoryNames;

    public IndexExclusionRules(IEnumerable<string> directoryNames)
    {
        ArgumentNullException.ThrowIfNull(directoryNames);
        _directoryNames = directoryNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static IndexExclusionRules Default { get; } = new(DefaultDirectoryNames);

    public bool ShouldExcludeDirectoryName(string directoryName)
    {
        return !string.IsNullOrWhiteSpace(directoryName)
            && _directoryNames.Contains(directoryName.Trim());
    }

    public bool ShouldExcludeDirectoryPath(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        return ShouldExcludeDirectoryName(Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath)));
    }
}
```

Modify `FallbackIndexProvider` constructor to accept rules and skip excluded directories before yielding/pushing:

```csharp
private readonly IndexExclusionRules _exclusionRules;

public FallbackIndexProvider()
    : this(IndexExclusionRules.Default)
{
}

public FallbackIndexProvider(IndexExclusionRules exclusionRules)
{
    _exclusionRules = exclusionRules ?? throw new ArgumentNullException(nameof(exclusionRules));
}
```

Inside directory loop:

```csharp
if (_exclusionRules.ShouldExcludeDirectoryPath(directory))
{
    continue;
}
```

- [ ] **Step 5: Run tests**

Run the same filtered command. Expected: PASS.

## Task 4: Program Temp Directory and Streaming UAC Import

**Files:**
- Modify: `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerClient.cs`
- Modify: `src/ListaryOpen.Infrastructure/Indexing/ElevatedIndexerProcessStartInfoFactory.cs`
- Modify: `src/ListaryOpen.Indexer.Elevated/ElevatedIndexerOutputPathValidator.cs`
- Modify: `src/ListaryOpen.Indexer.Elevated/Program.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/ElevatedIndexerClientTests.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/ElevatedIndexerOutputPathValidatorTests.cs`

- [ ] **Step 1: Write tests for configured temp directory**

Add a test that constructs `ElevatedIndexerClient` with a temp directory and asserts the created output/error paths are inside that directory. Update validator tests to require `allowedTempDirectory`.

- [ ] **Step 2: Write test for streaming import**

Add an `IElevatedIndexerProcess` test double that writes many JSON lines to the output file. The client should parse them and delete the files without using a single whole-file read path.

- [ ] **Step 3: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~ElevatedIndexerClientTests|FullyQualifiedName~ElevatedIndexerOutputPathValidatorTests"
```

Expected: failures until constructors, validator signatures, and parser are updated.

- [ ] **Step 4: Implement configured temp directory**

Add `indexerTempDirectory` to `ElevatedIndexerClient`, defaulting to `AppContext.BaseDirectory\data\tmp` through `AppDataPaths`.

Replace `CreateTempIndexerFilePath(string extension)` with:

```csharp
private string CreateTempIndexerFilePath(string extension)
{
    Directory.CreateDirectory(_indexerTempDirectory);
    return Path.Combine(_indexerTempDirectory, "listary-open-indexer-" + Guid.NewGuid() + extension);
}
```

- [ ] **Step 5: Implement streaming JSONL read**

Replace `ReadFileIfExistsAsync(outputPath, cancellationToken)` for success output with a line reader that calls `ParseRecord(line, lineNumber)` per line and returns a list. The first implementation may still return `IReadOnlyList<FileRecord>` to preserve provider interfaces, but it must not read the full text string.

- [ ] **Step 6: Update helper validator and Program args**

Change `scan-to-file` usage to:

```text
scan-to-file <root> <records-path> <error-path> <allowed-temp-directory>
```

Validate `recordsPath` and `errorPath` against the supplied allowed temp directory.

- [ ] **Step 7: Run tests**

Run filtered tests. Expected: PASS.

## Task 5: NTFS Directory-First Streaming Projection

**Files:**
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnJournalReader.cs`
- Modify: `src/ListaryOpen.Indexer.Elevated/Ntfs/NtfsUsnRecordProjector.cs`
- Test: `tests/ListaryOpen.Infrastructure.Tests/Indexing/NtfsUsnRecordProjectorTests.cs`

- [ ] **Step 1: Write tests for directory-only state and exclusions**

Add tests showing:

- A file under `node_modules` is skipped.
- A file under `keep` is yielded.
- Missing volume root record still resolves direct root children.

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
dotnet test tests\ListaryOpen.Infrastructure.Tests\ListaryOpen.Infrastructure.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~NtfsUsnRecordProjectorTests
```

Expected: exclusion-specific test fails.

- [ ] **Step 3: Implement directory-first projection**

Split projection into:

- Build directory map from directory entries.
- Resolve directory paths from parent links and volume root reference.
- Mark excluded directories.
- Stream records through resolved parent directories.

The implementation must not create a dictionary containing every file entry.

- [ ] **Step 4: Wire reader to two-pass enumeration**

`NtfsUsnJournalReader` should enumerate USN data once for directory map and once for streaming output. Keep `ReadEntries` only for tests or remove it once the streaming path is fully covered.

- [ ] **Step 5: Run tests**

Run projector tests. Expected: PASS.

## Task 6: Full Verification and Manual Checks

**Files:**
- No new production files expected.

- [ ] **Step 1: Run full tests**

Run:

```powershell
dotnet test ListaryOpen.sln -c Release --no-restore
```

Expected: all tests pass.

- [ ] **Step 2: Build Debug output**

Stop the current Debug app if needed, then run:

```powershell
dotnet build ListaryOpen.sln -c Debug --no-restore
```

Expected: build succeeds and writes updated app/helper outputs.

- [ ] **Step 3: Manual path checks**

Start:

```powershell
Start-Process .\src\ListaryOpen.App\bin\Debug\net8.0-windows\ListaryOpen.App.exe
```

Expected:

- No Settings window appears on normal startup.
- `src\ListaryOpen.App\bin\Debug\net8.0-windows\data\index.db` exists.
- Temporary helper files, if present during indexing, are under `data\tmp`.
- `%LOCALAPPDATA%\ListaryOpen\index.db` is not updated by the new run.

- [ ] **Step 4: Manual performance checks**

During indexing, run:

```powershell
Get-Process ListaryOpen.App,ListaryOpen.Indexer.Elevated -ErrorAction SilentlyContinue |
  Select-Object Id, ProcessName, @{Name='WorkingSetMB';Expression={[math]::Round($_.WorkingSet64/1MB,1)}}, @{Name='PrivateMB';Expression={[math]::Round($_.PrivateMemorySize64/1MB,1)}}, CPU
```

Expected: elevated helper memory is substantially lower than the previous roughly 1.1 GB peak.

## Self-Review

- Spec coverage: program data paths, tmp directory, quiet startup, exclusions, streaming import, helper memory optimization, and verification are covered.
- Placeholder scan: no incomplete placeholder markers remain.
- Type consistency: `AppDataPaths`, `IndexExclusionRules`, and existing provider/client names are used consistently.
