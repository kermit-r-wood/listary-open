using System.Text.Json;

namespace ListaryOpen.Infrastructure.Tests.Tools;

public sealed class AutomatedAcceptanceDefinitionTests
{
    [Fact]
    public void CoverageManifestTracksEveryConversationRegressionAreaWithExecutableEvidence()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPath("tests", "automation-coverage.json")));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var groups = root.GetProperty("featureGroups").EnumerateArray().ToArray();
        Assert.True(groups.Length >= 30);

        var ids = groups.Select(group => group.GetProperty("id").GetString()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        AssertContainsAll(
            ids,
            "appearance-theme-layout",
            "settings-language-save",
            "settings-drive-roots",
            "indexing-fast-uac",
            "indexing-elevated-real-helper",
            "github-release-check",
            "search-pinyin-multilingual",
            "search-preview",
            "explorer-repeat-focus-input",
            "task-manager-search",
            "dialog-native-file-jump-focus",
            "dialog-native-folder-focus",
            "dialog-repeat-input",
            "global-host-isolation",
            "global-hotkey-registration",
            "search-actions-quick-launch",
            "explorer-menu-selection",
            "quick-switch-folder-providers",
            "dialog-native-hook-plugins-no-fallback",
            "hook-ipc-security-bitness",
            "app-startup-single-instance-tray",
            "dpi-dispatcher-layout",
            "performance-data-recovery",
            "package-windows-distributable");

        var supportedSuites = new HashSet<string>(
            ["dotnet", "desktop", "elevatedDesktop", "elevatedIndexer", "packagedBlackbox", "native", "visual", "package"],
            StringComparer.Ordinal);
        foreach (var group in groups)
        {
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("requirement").GetString()));
            var evidence = group.GetProperty("evidence").EnumerateArray().ToArray();
            Assert.NotEmpty(evidence);
            foreach (var item in evidence)
            {
                var suite = item.GetProperty("suite").GetString() ?? string.Empty;
                Assert.Contains(suite, supportedSuites);
                if (suite == "package")
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("artifact").GetString()));
                }
                else
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("testNameContains").GetString()));
                    if (suite == "visual")
                    {
                        Assert.EndsWith(".png", item.GetProperty("screenshot").GetString(), StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
    }

    [Fact]
    public void AcceptanceRunnerExecutesBothArchitecturesDesktopTestsCoverageAndZipVerification()
    {
        var script = File.ReadAllText(RepositoryPath("tools", "Invoke-AutomatedAcceptance.ps1"));

        Assert.Contains("x86_64-pc-windows-gnullvm", script, StringComparison.Ordinal);
        Assert.Contains("i686-pc-windows-gnullvm", script, StringComparison.Ordinal);
        Assert.Contains("Category=DesktopIntegration", script, StringComparison.Ordinal);
        Assert.Contains("Category=ElevatedDesktopIntegration", script, StringComparison.Ordinal);
        Assert.Contains("Category=ElevatedIndexerIntegration", script, StringComparison.Ordinal);
        Assert.Contains("Category=ElevatedPackagedBlackboxE2E", script, StringComparison.Ordinal);
        Assert.Contains("elevated-indexer.trx", script, StringComparison.Ordinal);
        Assert.Contains("Category=VisualAcceptance", script, StringComparison.Ordinal);
        Assert.Contains("Get-PassedTrxTestNames", script, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = \"Continue\"", script, StringComparison.Ordinal);
        Assert.Contains("$exitCode = $LASTEXITCODE", script, StringComparison.Ordinal);
        Assert.Contains("Remove-DirectoryWithRetry", script, StringComparison.Ordinal);
        Assert.Contains("Clear-PackageDirectoryPreservingData", script, StringComparison.Ordinal);
        Assert.Contains("Preserving indexed data directory", script, StringComparison.Ordinal);
        Assert.Contains("Get-PackageFilesForArchive", script, StringComparison.Ordinal);
        Assert.Contains("Compress-PackageArchive", script, StringComparison.Ordinal);
        Assert.Contains("-DataDirectory $packageDataDirectory", script, StringComparison.Ordinal);
        Assert.Contains("waiting 5 seconds for transient file handles", script, StringComparison.Ordinal);
        Assert.Contains("[string]::Equals($_.Method, $evidence.testNameContains, [StringComparison]::Ordinal)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-like \"*$($evidence.testNameContains)*\"", script, StringComparison.Ordinal);
        Assert.Contains("tests\\visual-acceptance-matrix.json", script, StringComparison.Ordinal);
        Assert.Contains("tests\\desktop-visual-evidence.json", script, StringComparison.Ordinal);
        Assert.Contains("expectedVisualMatrix.screenshots.Count + $desktopVisualMatrix.artifacts.Count", script, StringComparison.Ordinal);
        Assert.Contains("dialogClosedWithoutOpening", script, StringComparison.Ordinal);
        Assert.Contains("screenshotSha256", script, StringComparison.Ordinal);
        Assert.Contains("compiled-dialog-plugin-real-nonstandard-host-structured-direct-navigation", script, StringComparison.Ordinal);
        Assert.Contains("packaged-tray-settings-persistence-across-restart", script, StringComparison.Ordinal);
        Assert.Contains("packaged-elevated-global-hotkey-search-preview-menu-and-quick-launch", script, StringComparison.Ordinal);
        Assert.Contains("published-app-real-ctrl-g-compiled-plugin-nonstandard-direct-navigation", script, StringComparison.Ordinal);
        Assert.Contains("complete 22-screenshot acceptance matrix", script, StringComparison.Ordinal);
        Assert.Contains("SendInput|keybd_event|WM_KEYDOWN|WM_KEYUP|WM_CHAR|WM_SETTEXT|EM_REPLACESEL|ValuePattern|Clipboard|SendKeys|SetWindowText", script, StringComparison.Ordinal);
        Assert.Contains("Assert.True\\(SetForegroundWindow\\(", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained\", \"true", script, StringComparison.Ordinal);
        Assert.Contains("tests\\automation-coverage.json", script, StringComparison.Ordinal);
        Assert.Contains("ListaryOpen-win-x64.zip", script, StringComparison.Ordinal);
        Assert.Contains("[IO.Compression.ZipFile]::OpenRead", script, StringComparison.Ordinal);
        Assert.Contains("hooks/x64/ListaryOpen.Hook.dll", script, StringComparison.Ordinal);
        Assert.Contains("hooks/x86/ListaryOpen.Hook.dll", script, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualEvidenceMatricesAreRepositoryOwnedUniqueAndCarryEvidenceLevels()
    {
        using var productDocument = JsonDocument.Parse(
            File.ReadAllText(RepositoryPath("tests", "visual-acceptance-matrix.json")));
        var productRoot = productDocument.RootElement;
        Assert.Equal(1, productRoot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("DesktopBitBlt", productRoot.GetProperty("captureMethod").GetString());
        var productStates = productRoot.GetProperty("screenshots").EnumerateArray().ToArray();
        Assert.Equal(22, productStates.Length);
        Assert.Equal(productStates.Length, productStates.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(productStates.Length, productStates.Select(item => item.GetProperty("file").GetString()).Distinct().Count());
        Assert.Contains(productStates, item => item.GetProperty("id").GetString() == "21-quick-switch-collapsed-light-en");
        Assert.Contains(productStates, item => item.GetProperty("id").GetString() == "22-quick-switch-expanded-light-en");
        var visualSource = File.ReadAllText(
            RepositoryPath("tests", "ListaryOpen.VisualTests", "VisualAcceptanceTests.cs"));
        Assert.Contains("AssertQuickSwitchHasNoBrightOuterBand", visualSource, StringComparison.Ordinal);

        using var desktopDocument = JsonDocument.Parse(
            File.ReadAllText(RepositoryPath("tests", "desktop-visual-evidence.json")));
        var desktopRoot = desktopDocument.RootElement;
        Assert.Equal(2, desktopRoot.GetProperty("schemaVersion").GetInt32());
        var desktopArtifacts = desktopRoot.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.NotEmpty(desktopArtifacts);
        Assert.Equal(desktopArtifacts.Length, desktopArtifacts.Select(item => item.GetProperty("id").GetString()).Distinct().Count());
        Assert.Equal(desktopArtifacts.Length, desktopArtifacts.Select(item => item.GetProperty("file").GetString()).Distinct().Count());
        Assert.All(desktopArtifacts, item =>
        {
            Assert.StartsWith("ListaryOpen.IntegrationTests.", item.GetProperty("testClass").GetString(), StringComparison.Ordinal);
            Assert.Contains(
                item.GetProperty("suite").GetString(),
                new[] { "desktop", "elevatedDesktop", "packagedBlackbox" });
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("testMethod").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("evidenceLevel").GetString()));
            Assert.True(item.GetProperty("minimumPassedInstances").GetInt32() > 0);
        });
        Assert.Contains(desktopArtifacts, item =>
            item.GetProperty("file").GetString() == "27-real-explorer-selection.png" &&
            item.GetProperty("evidenceLevel").GetString() == "real-system-ui");
        Assert.Contains(desktopArtifacts, item =>
            item.GetProperty("file").GetString() == "30-packaged-firefox-direct-hotkey.png" &&
            item.GetProperty("evidenceLevel").GetString() == "packaged-app-blackbox-e2e");
        Assert.Contains(desktopArtifacts, item =>
            item.GetProperty("file").GetString() == "32-packaged-nonstandard-plugin-direct.png" &&
            item.GetProperty("evidenceLevel").GetString() == "packaged-app-blackbox-e2e");
    }

    [Fact]
    public void PackageWorkflowUsesTheSameAcceptanceRunnerAndUploadsItsEvidence()
    {
        var workflow = File.ReadAllText(RepositoryPath(".github", "workflows", "package.yml"));

        Assert.Contains("tools\\Invoke-AutomatedAcceptance.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/acceptance-results", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/ListaryOpen-win-x64.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("$packageData", workflow, StringComparison.Ordinal);
        Assert.Contains("Compress-Archive -LiteralPath $archiveItems", workflow, StringComparison.Ordinal);
        Assert.Contains("ZIP must not contain local index data", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void FirefoxPickerSetupUsesCtrlOAndHasANoHookBaseline()
    {
        var desktop = File.ReadAllText(
            RepositoryPath("tests", "ListaryOpen.IntegrationTests", "FirefoxQuickSwitchIntegrationTests.cs"));
        var packaged = File.ReadAllText(
            RepositoryPath("tests", "ListaryOpen.IntegrationTests", "PackagedFirefoxBlackboxIntegrationTests.cs"));

        Assert.Contains("FirefoxCtrlOOpensNativeDialogWithoutHook", desktop, StringComparison.Ordinal);
        Assert.Contains("SendCtrlO();", desktop, StringComparison.Ordinal);
        Assert.Contains("SendCtrlO();", packaged, StringComparison.Ordinal);
        Assert.Contains("dialogClosedWithoutOpening", desktop, StringComparison.Ordinal);
        Assert.Contains("dialogClosedWithoutOpening", packaged, StringComparison.Ordinal);
        Assert.DoesNotContain("clickOffsets", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("(135, 102)", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("(135, 102)", packaged, StringComparison.Ordinal);
    }

    private static void AssertContainsAll(IEnumerable<string?> actual, params string[] expected)
    {
        var set = actual.Where(value => value is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var value in expected)
        {
            Assert.Contains(value, set);
        }
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ListaryOpen.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. parts]);
    }
}
