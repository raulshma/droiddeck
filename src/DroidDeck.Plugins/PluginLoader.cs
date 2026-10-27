using DroidDeck.Core.Infrastructure;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Plugins;

/// <summary>
/// The outcome of loading one plugin.
/// </summary>
/// <param name="Plugin">The loaded plugin, or <see langword="null"/> when loading failed.</param>
/// <param name="Validation">The validation issues found while loading.</param>
public sealed record PluginLoadResult(LoadedPlugin? Plugin, PluginValidationResult Validation)
{
    /// <summary>Gets a value indicating whether the plugin loaded.</summary>
    public bool Succeeded => Plugin is not null;
}

/// <summary>
/// Validates, loads, and unloads plugins. Each plugin receives an isolated,
/// collectible assembly load context.
/// </summary>
public sealed class PluginLoader : IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly string _hostVersion;
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates a loader that registers plugin extensions into a registry.</summary>
    public PluginLoader(PluginRegistry registry, ILoggerFactory loggerFactory, string? hostVersion = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("DroidDeck.Plugins");
        _hostVersion = hostVersion ?? AppInfo.Version;
    }

    /// <summary>Gets the loaded plugins in load order.</summary>
    public IReadOnlyList<LoadedPlugin> Plugins
    {
        get
        {
            lock (_sync)
            {
                return _plugins.ToArray();
            }
        }
    }

    /// <summary>
    /// Loads every plugin directory under <paramref name="pluginsRoot"/> that contains a manifest.
    /// </summary>
    public async Task<IReadOnlyList<PluginLoadResult>> LoadInstalledAsync(
        string pluginsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = new List<PluginLoadResult>();
        if (!Directory.Exists(pluginsRoot))
        {
            return results;
        }

        var directories = Directory
            .EnumerateDirectories(pluginsRoot)
            .Where(directory => File.Exists(PluginManifestReader.GetManifestPath(directory)))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await LoadAsync(directory, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Validates and loads a plugin from a directory containing a manifest.
    /// </summary>
    public async Task<PluginLoadResult> LoadAsync(
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(Path.GetFullPath(pluginDirectory), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Shuts down and unloads a plugin. Returns <see langword="false"/> when it is not loaded.
    /// </summary>
    public async Task<bool> UnloadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LoadedPlugin? plugin;
            lock (_sync)
            {
                plugin = _plugins.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
                if (plugin is null)
                {
                    return false;
                }

                _plugins.Remove(plugin);
            }

            await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        LoadedPlugin[] plugins;
        lock (_sync)
        {
            plugins = [.. _plugins];
            _plugins.Clear();
        }

        foreach (var plugin in plugins.Reverse())
        {
            await plugin.ShutdownAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private async Task<PluginLoadResult> LoadCoreAsync(string pluginDirectory, CancellationToken cancellationToken)
    {
        var read = await PluginManifestReader.ReadAsync(pluginDirectory, cancellationToken).ConfigureAwait(false);
        if (read.Manifest is null)
        {
            LogIssues(read.Validation, pluginDirectory);
            return new PluginLoadResult(null, read.Validation);
        }

        var manifest = read.Manifest;
        var validation = PluginManifestValidator.Validate(manifest, pluginDirectory, _hostVersion);
        if (!validation.IsValid)
        {
            LogIssues(validation, pluginDirectory);
            return new PluginLoadResult(null, validation);
        }

        if (IsLoaded(manifest.Id))
        {
            validation.Error("plugin.id.duplicate", $"A plugin with id '{manifest.Id}' is already loaded.");
            LogIssues(validation, pluginDirectory);
            return new PluginLoadResult(null, validation);
        }

        PluginLoadContext? loadContext = null;
        JsonPluginSettings? settings = null;
        IPlugin? instance = null;
        try
        {
            var logger = _loggerFactory.CreateLogger($"DroidDeck.Plugins.{manifest.Id}");
            loadContext = new PluginLoadContext(Path.Combine(pluginDirectory, manifest.EntryAssembly), manifest.Id);
            var assembly = loadContext.LoadFromAssemblyPath(Path.Combine(pluginDirectory, manifest.EntryAssembly));

            var entryType = assembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                validation.Error(
                    "plugin.entryType.missing",
                    $"The entry type '{manifest.EntryType}' was not found in '{manifest.EntryAssembly}'.");
                LogIssues(validation, pluginDirectory);
                await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
                return new PluginLoadResult(null, validation);
            }

            if (!typeof(IPlugin).IsAssignableFrom(entryType))
            {
                validation.Error(
                    "plugin.entryType.invalid",
                    $"The entry type '{manifest.EntryType}' does not implement {nameof(IPlugin)}.");
                LogIssues(validation, pluginDirectory);
                await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
                return new PluginLoadResult(null, validation);
            }

            if (Activator.CreateInstance(entryType) is not IPlugin created)
            {
                validation.Error(
                    "plugin.entryType.creationFailed",
                    $"The entry type '{manifest.EntryType}' could not be instantiated.");
                LogIssues(validation, pluginDirectory);
                await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
                return new PluginLoadResult(null, validation);
            }

            instance = created;

            var dataDirectory = Path.Combine(pluginDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            settings = new JsonPluginSettings(Path.Combine(pluginDirectory, "settings.json"), logger);

            var loaded = new LoadedPlugin(manifest, pluginDirectory, instance, loadContext, settings, _registry, logger);
            var context = new PluginContext(
                loaded,
                logger,
                _registry.CreateScope(manifest.Id),
                settings,
                dataDirectory);

            await instance.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _plugins.Add(loaded);
            }

            LogIssues(validation, pluginDirectory);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Loaded plugin {PluginId} {Version} from '{Path}'.",
                    manifest.Id,
                    manifest.Version,
                    pluginDirectory);
            }

            return new PluginLoadResult(loaded, validation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Plugin '{PluginId}' failed to load from '{Path}'.",
                manifest.Id,
                pluginDirectory);
            validation.Error("plugin.load.failed", $"The plugin could not be loaded: {exception.Message}");
            await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
            return new PluginLoadResult(null, validation);
        }
    }

    private async Task CleanupFailedLoadAsync(
        string pluginId,
        IPlugin? instance,
        PluginLoadContext? loadContext,
        JsonPluginSettings? settings)
    {
        if (instance is not null)
        {
            try
            {
                await instance.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Plugin '{PluginId}' threw while cleaning up a failed load.", pluginId);
            }
        }

        _registry.RemoveAll(pluginId);
        loadContext?.Unload();
        settings?.Dispose();
    }

    private bool IsLoaded(string pluginId)
    {
        lock (_sync)
        {
            return _plugins.Exists(
                candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void LogIssues(PluginValidationResult validation, string pluginDirectory)
    {
        foreach (var issue in validation.Issues)
        {
            if (issue.Severity == PluginValidationSeverity.Error)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(
                        "Plugin validation error in '{Path}': {Code} {Message}",
                        pluginDirectory,
                        issue.Code,
                        issue.Message);
                }
            }
            else if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "Plugin validation warning in '{Path}': {Code} {Message}",
                    pluginDirectory,
                    issue.Code,
                    issue.Message);
            }
        }
    }
}
