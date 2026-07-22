using System.Text.Json;
using ListaryOpen.Infrastructure.Dialog;

namespace ListaryOpen.Infrastructure.Tests.Dialog;

public sealed class DialogPluginLoaderTests
{
    [Fact]
    public void MissingRootLoadsNoPluginsWithoutCreatingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var result = DialogPluginLoader.LoadFromRoot(root);

        Assert.Empty(result.Plugins);
        Assert.Empty(result.Diagnostics);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void InvalidApiVersionIsRejectedWithDiagnostic()
    {
        using var root = new TemporaryDirectory();
        WriteManifest(root.Path, new
        {
            id = "fixture.plugin",
            version = "1.0.0",
            apiVersion = DialogPluginLoader.CurrentApiVersion + 1,
            entryAssembly = "fixture.dll",
            entryType = "Fixture.Plugin",
            supportedHosts = new[] { "fixture.exe" }
        });

        var result = DialogPluginLoader.LoadFromRoot(root.Path);

        Assert.Empty(result.Plugins);
        Assert.Contains("Unsupported apiVersion", Assert.Single(result.Diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    public void EntryAssemblyCannotEscapePluginDirectory()
    {
        using var root = new TemporaryDirectory();
        WriteManifest(root.Path, new
        {
            id = "fixture.plugin",
            version = "1.0.0",
            apiVersion = DialogPluginLoader.CurrentApiVersion,
            entryAssembly = "..\\outside.dll",
            entryType = "Fixture.Plugin",
            supportedHosts = new[] { "fixture.exe" }
        });

        var result = DialogPluginLoader.LoadFromRoot(root.Path);

        Assert.Empty(result.Plugins);
        Assert.Single(result.Diagnostics);
    }

    [Fact]
    public void ValidManifestLoadsCompiledPluginThroughPluginLoadContext()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "plugin-fixture");
        var fixtureAssembly = Path.Combine(fixtureRoot, "ListaryOpen.DialogPlugin.TestFixture.dll");
        var fixtureManifest = Path.Combine(fixtureRoot, DialogPluginLoader.ManifestFileName);
        Assert.True(File.Exists(fixtureAssembly), $"Compiled plugin fixture is missing: {fixtureAssembly}");
        Assert.True(File.Exists(fixtureManifest), $"Plugin fixture manifest is missing: {fixtureManifest}");

        var result = DialogPluginLoader.LoadFromRoot(fixtureRoot);

        Assert.Empty(result.Diagnostics);
        var plugin = Assert.Single(result.Plugins);
        Assert.Equal("listaryopen.fixture.custom-browser", plugin.Id);
        Assert.Equal(
            "ListaryOpen.DialogPlugin.TestFixture.CustomBrowserDialogPlugin",
            plugin.GetType().FullName);
        Assert.NotEqual(typeof(DialogPluginLoader).Assembly, plugin.GetType().Assembly);
    }

    private static void WriteManifest(string directory, object value) =>
        File.WriteAllText(
            Path.Combine(directory, DialogPluginLoader.ManifestFileName),
            JsonSerializer.Serialize(value));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() => Path = Directory.CreateTempSubdirectory("listary-plugin-").FullName;
        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
