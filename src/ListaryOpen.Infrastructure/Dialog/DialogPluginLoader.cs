using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;

namespace ListaryOpen.Infrastructure.Dialog;

public sealed record DialogPluginLoadResult(
    IReadOnlyList<IDialogJumpPlugin> Plugins,
    IReadOnlyList<string> Diagnostics);

public static partial class DialogPluginLoader
{
    public const int CurrentApiVersion = 1;
    public const string ManifestFileName = "dialog-plugin.json";

    public static string DefaultPluginRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ListaryOpen",
        "Plugins");

    public static DialogPluginLoadResult LoadFromRoot(string pluginRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        var plugins = new List<IDialogJumpPlugin>();
        var diagnostics = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(pluginRoot))
        {
            return new DialogPluginLoadResult(plugins, diagnostics);
        }

        foreach (var manifestPath in Directory.EnumerateFiles(
                     pluginRoot,
                     ManifestFileName,
                     SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var loaded = LoadManifest(manifestPath);
                if (!ids.Add(loaded.Id))
                {
                    diagnostics.Add($"Rejected duplicate dialog plugin id '{loaded.Id}' at '{manifestPath}'.");
                    continue;
                }
                plugins.Add(loaded.Plugin);
            }
            catch (Exception exception)
            {
                diagnostics.Add($"Rejected dialog plugin manifest '{manifestPath}': {exception.Message}");
            }
        }
        return new DialogPluginLoadResult(plugins, diagnostics);
    }

    private static LoadedPlugin LoadManifest(string manifestPath)
    {
        var manifest = JsonSerializer.Deserialize<DialogPluginManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Manifest is empty.");
        if (manifest.ApiVersion != CurrentApiVersion)
        {
            throw new InvalidDataException(
                $"Unsupported apiVersion {manifest.ApiVersion}; expected {CurrentApiVersion}.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Id) || !PluginIdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidDataException("Plugin id must contain only lowercase letters, digits, dots, or hyphens.");
        }
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly)
            || string.IsNullOrWhiteSpace(manifest.EntryType)
            || manifest.SupportedHosts is not { Length: > 0 })
        {
            throw new InvalidDataException(
                "Manifest requires version, entryAssembly, entryType, and at least one supportedHosts entry.");
        }

        var pluginDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var assemblyPath = Path.GetFullPath(Path.Combine(pluginDirectory, manifest.EntryAssembly));
        if (!assemblyPath.StartsWith(
                pluginDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(assemblyPath))
        {
            throw new InvalidDataException("entryAssembly must be an existing file inside the plugin directory.");
        }

        var loadContext = new DialogPluginLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetType(manifest.EntryType, throwOnError: true, ignoreCase: false)!;
        if (!typeof(IDialogJumpPlugin).IsAssignableFrom(type) || type.IsAbstract)
        {
            throw new InvalidDataException("entryType must be a concrete IDialogJumpPlugin implementation.");
        }
        var plugin = (IDialogJumpPlugin?)Activator.CreateInstance(type)
            ?? throw new InvalidDataException("entryType could not be constructed with a parameterless constructor.");
        if (!string.Equals(plugin.Id, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Runtime plugin id '{plugin.Id}' does not exactly match manifest id '{manifest.Id}'.");
        }
        return new LoadedPlugin(manifest.Id, plugin, loadContext);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    private sealed record LoadedPlugin(
        string Id,
        IDialogJumpPlugin Plugin,
        AssemblyLoadContext LoadContext);

    private sealed class DialogPluginLoadContext : AssemblyLoadContext
    {
        private static readonly string ContractAssemblyName = typeof(IDialogJumpPlugin).Assembly.GetName().Name!;
        private readonly AssemblyDependencyResolver _resolver;

        public DialogPluginLoadContext(string entryAssemblyPath)
            : base($"ListaryOpen.DialogPlugin:{entryAssemblyPath}", isCollectible: false) =>
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, ContractAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private sealed record DialogPluginManifest(
        string Id,
        string Version,
        int ApiVersion,
        string EntryAssembly,
        string EntryType,
        string[] SupportedHosts);
}
