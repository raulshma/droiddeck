using System.Diagnostics;
using System.Reflection;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Security;
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

    /// <summary>Gets the manifest the result belongs to, when it could be read.</summary>
    public PluginManifest? Manifest { get; init; }

    /// <summary>Gets how long loading took; zero when the plugin did not load.</summary>
    public TimeSpan LoadDuration { get; init; }
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
    private readonly ISecretStore? _secretStore;
    private readonly IPluginDialogServiceProvider? _dialogs;
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates a loader that registers plugin extensions into a registry.</summary>
    /// <param name="registry">The extension registry plugins register into.</param>
    /// <param name="loggerFactory">The factory for host and plugin-scoped loggers.</param>
    /// <param name="hostVersion">The host version to validate against; defaults to the running version.</param>
    /// <param name="secretStore">The DPAPI secret store that backs plugin secret settings.</param>
    /// <param name="dialogs">The provider that gives each plugin its scoped dialog service.</param>
    public PluginLoader(
        PluginRegistry registry,
        ILoggerFactory loggerFactory,
        string? hostVersion = null,
        ISecretStore? secretStore = null,
        IPluginDialogServiceProvider? dialogs = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("DroidDeck.Plugins");
        _hostVersion = hostVersion ?? AppInfo.Version;
        _secretStore = secretStore;
        _dialogs = dialogs;
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
    /// Dependencies are resolved before dependents regardless of directory order;
    /// plugins whose dependencies are missing or cyclic are rejected with a clear
    /// reason instead of loading.
    /// </summary>
    /// <param name="pluginsRoot">The directory that contains one subdirectory per plugin.</param>
    /// <param name="disabledPluginIds">Plugin identifiers to skip this launch.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public async Task<IReadOnlyList<PluginLoadResult>> LoadInstalledAsync(
        string pluginsRoot,
        IReadOnlySet<string>? disabledPluginIds = null,
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

        var plan = new List<(string Directory, PluginManifestReadResult Read)>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            plan.Add(
                (directory,
                await PluginManifestReader.ReadAsync(directory, cancellationToken).ConfigureAwait(false)));
        }

        var (order, cycles) = PlanLoadOrder(plan, disabledPluginIds);
        foreach (var (directory, _) in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await LoadAsync(directory, cancellationToken).ConfigureAwait(false));
        }

        foreach (var (directory, read) in cycles)
        {
            var validation = new PluginValidationResult();
            var members = cycles
                .Select(cycle => cycle.Read.Manifest!.Id)
                .Order(StringComparer.OrdinalIgnoreCase);
            validation.Error(
                "plugin.dependencies.cycle",
                $"The plugin takes part in a dependency cycle with: {string.Join(", ", members)}.");
            LogIssues(validation, directory);
            results.Add(new PluginLoadResult(null, validation) { Manifest = read.Manifest });
        }

        return results;
    }

    /// <summary>
    /// Orders readable manifests so dependencies load before dependents, skipping
    /// disabled plugins. Returns the ordered entries plus the entries caught in a
    /// dependency cycle, which are rejected without loading their assemblies.
    /// Unreadable manifests keep their position and fail when loaded.
    /// </summary>
    private static (List<(string Directory, PluginManifestReadResult Read)> Order, List<(string Directory, PluginManifestReadResult Read)> Cycles)
        PlanLoadOrder(List<(string Directory, PluginManifestReadResult Read)> plan, IReadOnlySet<string>? disabledPluginIds)
    {
        var order = new List<(string, PluginManifestReadResult)>();
        var cycles = new List<(string, PluginManifestReadResult)>();
        var pending = new List<(string Directory, PluginManifestReadResult Read)>();
        var byId = new Dictionary<string, (string Directory, PluginManifest Manifest)>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, read) in plan)
        {
            if (read.Manifest is null)
            {
                order.Add((directory, read));
                continue;
            }

            if (disabledPluginIds is not null && disabledPluginIds.Contains(read.Manifest.Id))
            {
                continue;
            }

            pending.Add((directory, read));
            byId.TryAdd(read.Manifest.Id, (directory, read.Manifest));
        }

        while (pending.Count > 0)
        {
            var index = pending.FindIndex(
                entry => entry.Read.Manifest!.Dependencies.All(
                    dependency => !byId.ContainsKey(dependency.Id) || visited.Contains(dependency.Id)));
            if (index < 0)
            {
                cycles.AddRange(pending);
                break;
            }

            var entry = pending[index];
            pending.RemoveAt(index);
            visited.Add(entry.Read.Manifest!.Id);
            order.Add(entry);
        }

        return (order, cycles);
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
            return new PluginLoadResult(null, validation) { Manifest = manifest };
        }

        if (IsLoaded(manifest.Id))
        {
            validation.Error("plugin.id.duplicate", $"A plugin with id '{manifest.Id}' is already loaded.");
            LogIssues(validation, pluginDirectory);
            return new PluginLoadResult(null, validation) { Manifest = manifest };
        }

        ValidateDependencies(manifest, validation);
        if (!validation.IsValid)
        {
            LogIssues(validation, pluginDirectory);
            return new PluginLoadResult(null, validation) { Manifest = manifest };
        }

        var startedAt = Stopwatch.GetTimestamp();
        PluginLoadContext? loadContext = null;
        IPluginSettings? settings = null;
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
                return new PluginLoadResult(null, validation) { Manifest = manifest };
            }

            if (!typeof(IPlugin).IsAssignableFrom(entryType))
            {
                validation.Error(
                    "plugin.entryType.invalid",
                    $"The entry type '{manifest.EntryType}' does not implement {nameof(IPlugin)}.");
                LogIssues(validation, pluginDirectory);
                await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
                return new PluginLoadResult(null, validation) { Manifest = manifest };
            }

            if (Activator.CreateInstance(entryType) is not IPlugin created)
            {
                validation.Error(
                    "plugin.entryType.creationFailed",
                    $"The entry type '{manifest.EntryType}' could not be instantiated.");
                LogIssues(validation, pluginDirectory);
                await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
                return new PluginLoadResult(null, validation) { Manifest = manifest };
            }

            instance = created;

            var dataDirectory = Path.Combine(pluginDirectory, "data");
            Directory.CreateDirectory(dataDirectory);
            settings = await CreateSettingsAsync(manifest, pluginDirectory, logger, validation, cancellationToken)
                .ConfigureAwait(false);

            var loaded = new LoadedPlugin(manifest, pluginDirectory, instance, loadContext, settings, _registry, logger);
            var context = new PluginContext(
                loaded,
                logger,
                _registry.CreateScope(manifest.Id),
                settings,
                dataDirectory,
                _dialogs?.CreateScope(manifest.Id) ?? NullPluginDialogService.Instance);

            await instance.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _plugins.Add(loaded);
            }

            LogIssues(validation, pluginDirectory);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Loaded plugin {PluginId} {Version} from '{Path}' in {DurationMs:F1} ms.",
                    manifest.Id,
                    manifest.Version,
                    pluginDirectory,
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }

            return new PluginLoadResult(loaded, validation)
            {
                Manifest = manifest,
                LoadDuration = Stopwatch.GetElapsedTime(startedAt),
            };
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
            // Construction failures arrive wrapped; the manager shows the
            // plugin's own error message.
            var reason = exception is TargetInvocationException
                ? exception.InnerException?.Message ?? exception.Message
                : exception.Message;
            validation.Error("plugin.load.failed", $"The plugin could not be loaded: {reason}");
            await CleanupFailedLoadAsync(manifest.Id, instance, loadContext, settings).ConfigureAwait(false);
            return new PluginLoadResult(null, validation) { Manifest = manifest };
        }
    }

    /// <summary>
    /// Creates the plugin settings store. A plugin that ships a readable
    /// settings.schema.json gets the schema-driven store with secret support; a
    /// plugin without one, or with a schema outside the supported subset, keeps
    /// the raw store and the schema problem is reported as a warning.
    /// </summary>
    private async Task<IPluginSettings> CreateSettingsAsync(
        PluginManifest manifest,
        string pluginDirectory,
        ILogger logger,
        PluginValidationResult validation,
        CancellationToken cancellationToken)
    {
        var settingsPath = Path.Combine(pluginDirectory, "settings.json");
        if (!File.Exists(PluginSettingsSchemaReader.GetSchemaPath(pluginDirectory)))
        {
            return new JsonPluginSettings(settingsPath, logger);
        }

        var read = await PluginSettingsSchemaReader.ReadAsync(pluginDirectory, cancellationToken).ConfigureAwait(false);
        if (read.Schema is null)
        {
            var reason = read.Validation.Issues.FirstOrDefault(static issue =>
                issue.Severity == PluginValidationSeverity.Error)?.Message ?? "The schema is invalid.";
            validation.Warning(
                "plugin.settingsSchema.invalid",
                $"The settings schema was not used: {reason} The plugin keeps its raw settings store.");
            return new JsonPluginSettings(settingsPath, logger);
        }

        if (read.Schema.Fields.Any(static field => field.Type == PluginSettingsFieldType.Secret) &&
            _secretStore is null)
        {
            validation.Warning(
                "plugin.settingsSchema.noSecretStore",
                "The plugin declares secret settings, but no secret store is available, so its " +
                    "settings schema was not used. The plugin keeps its raw settings store.");
            return new JsonPluginSettings(settingsPath, logger);
        }

        return new SchemaPluginSettings(
            settingsPath,
            read.Schema,
            manifest.Id,
            _secretStore!,
            logger);
    }

    /// <summary>
    /// Resolves manifest dependencies against the plugins that are already
    /// loaded. Failures carry a reason naming the missing dependency and the
    /// required version so the plugin manager can show it.
    /// </summary>
    private void ValidateDependencies(PluginManifest manifest, PluginValidationResult validation)
    {
        foreach (var dependency in manifest.Dependencies)
        {
            var loaded = FindLoaded(dependency.Id);
            if (loaded is null)
            {
                validation.Error(
                    "plugin.dependencies.unresolved",
                    $"Missing dependency {dependency.Id} {dependency.MinVersion}+. Install it and restart DroidDeck.");
                continue;
            }

            if (SemanticVersion.TryParse(dependency.MinVersion, out var minimum) &&
                SemanticVersion.TryParse(loaded.Version, out var actual) &&
                actual < minimum)
            {
                validation.Error(
                    "plugin.dependencies.incompatible",
                    $"The dependency {dependency.Id} must be version {dependency.MinVersion} or newer, " +
                    $"but the installed version is {actual}.");
            }
        }
    }

    private async Task CleanupFailedLoadAsync(
        string pluginId,
        IPlugin? instance,
        PluginLoadContext? loadContext,
        IPluginSettings? settings)
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
        if (settings is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private bool IsLoaded(string pluginId)
    {
        lock (_sync)
        {
            return _plugins.Exists(
                candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private LoadedPlugin? FindLoaded(string pluginId)
    {
        lock (_sync)
        {
            return _plugins.FirstOrDefault(
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
