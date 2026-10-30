using System.Runtime.Loader;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Plugins;

/// <summary>
/// A plugin instance that has been loaded into its own assembly load context.
/// </summary>
public sealed class LoadedPlugin : IPluginInfo, IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly IPluginSettings _settings;
    private readonly ILogger _logger;
    private bool _shutdown;

    internal LoadedPlugin(
        PluginManifest manifest,
        string installPath,
        IPlugin instance,
        AssemblyLoadContext loadContext,
        IPluginSettings settings,
        PluginRegistry registry,
        ILogger logger)
    {
        Manifest = manifest;
        InstallPath = installPath;
        Instance = instance;
        LoadContext = loadContext;
        _settings = settings;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Gets the plugin manifest.</summary>
    public PluginManifest Manifest { get; }

    /// <summary>Gets the plugin instance.</summary>
    public IPlugin Instance { get; }

    /// <summary>Gets the load context that owns the plugin assemblies.</summary>
    public AssemblyLoadContext LoadContext { get; }

    /// <summary>Gets the plugin's private settings store.</summary>
    public IPluginSettings Settings => _settings;

    /// <summary>Gets the capabilities declared by the manifest.</summary>
    public PluginCapability Capabilities => Manifest.KnownCapabilities;

    /// <inheritdoc />
    public string Id => Manifest.Id;

    /// <inheritdoc />
    public string Name => Manifest.Name;

    /// <inheritdoc />
    public string Version => Manifest.Version;

    /// <inheritdoc />
    public string ApiVersion => Manifest.ApiVersion;

    /// <inheritdoc />
    public string InstallPath { get; }

    /// <summary>
    /// Removes the plugin's extensions while keeping its assemblies loaded. A
    /// restart is required to unload a disabled plugin; the plugin is shut down
    /// cleanly when the host exits.
    /// </summary>
    public void Disable()
    {
        if (_shutdown)
        {
            return;
        }

        _registry.RemoveAll(Id);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Disabled plugin {PluginId}. Its extensions were removed; a restart is required to unload it.",
                Id);
        }
    }

    /// <summary>
    /// Shuts the plugin down, removes its extensions, and unloads its assemblies.
    /// Plugin failures are logged and never propagate to the host.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        try
        {
            await Instance.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Plugin '{PluginId}' threw while shutting down.", Id);
        }
        finally
        {
            _registry.RemoveAll(Id);
            if (_settings is IDisposable disposable)
            {
                disposable.Dispose();
            }

            LoadContext.Unload();
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Unloaded plugin {PluginId}.", Id);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    /// <inheritdoc />
    public override string ToString() => $"{Name} {Version} ({Id})";
}
