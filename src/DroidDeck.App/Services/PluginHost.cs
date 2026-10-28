using System.Text;
using DroidDeck.Core;
using DroidDeck.Plugins;
using DroidDeck.Plugins.BuiltIns;
using Microsoft.Extensions.Logging;

namespace DroidDeck.App.Services;

/// <summary>
/// Owns plugin discovery and load results for the shell, extension page, and
/// diagnostics report.
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly PluginLoader _loader;
    private readonly AppPaths _paths;
    private readonly ILogger<PluginHost> _logger;
    private readonly object _sync = new();
    private readonly HashSet<string> _disabledPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginExtensionCatalog _catalog;
    private readonly PluginHealthTracker _health;
    private readonly PluginActionInvoker _invoker;
    private IReadOnlyList<PluginLoadResult> _results = [];
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a plugin host.</summary>
    public PluginHost(PluginRegistry registry, PluginLoader loader, AppPaths paths, ILogger<PluginHost> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _catalog = new PluginExtensionCatalog(registry);
        _health = new PluginHealthTracker();
        _invoker = new PluginActionInvoker(_health);
    }

    /// <summary>Gets the shared extension registry.</summary>
    public PluginRegistry Registry => _registry;

    /// <summary>Gets device actions, tabs, and toolbar actions in host order.</summary>
    public PluginExtensionCatalog Extensions => _catalog;

    /// <summary>Gets plugin health snapshots.</summary>
    public PluginHealthTracker Health => _health;

    /// <summary>Gets the timeout-isolated plugin invoker.</summary>
    public PluginActionInvoker Invoker => _invoker;

    /// <summary>Gets whether plugins were skipped for this launch.</summary>
    public bool PluginsDisabled { get; private set; }

    /// <summary>Gets why plugins were skipped, if they were.</summary>
    public string? DisabledReason { get; private set; }

    /// <summary>Gets the results of the last plugin scan.</summary>
    public IReadOnlyList<PluginLoadResult> Results
    {
        get
        {
            lock (_sync)
            {
                return _results;
            }
        }
    }

    /// <summary>Gets the number of successfully loaded plugins.</summary>
    public int LoadedCount => Results.Count(result => result.Succeeded && !IsDisabled(result.Plugin!.Id));

    /// <summary>Gets whether a plugin has been disabled in this launch.</summary>
    public bool IsDisabled(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _disabledPlugins.Contains(pluginId);
        }
    }

    /// <summary>Raised after a plugin scan completes.</summary>
    public event EventHandler? ResultsChanged;

    /// <summary>Registers built-ins and loads installed plugins unless disabled.</summary>
    public async Task InitializeAsync(bool disabled, string? disabledReason, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BuiltInExtensions.Register(_registry, _paths);
        PluginsDisabled = disabled;
        DisabledReason = disabledReason;
        if (disabled)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Plugins disabled: {Reason}", disabledReason);
            }

            RaiseChanged();
            return;
        }

        try
        {
            var results = await _loader.LoadInstalledAsync(_paths.PluginsDirectory, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                _results = results;
                _initialized = true;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Plugin scan complete: {Loaded} loaded, {Failed} failed.",
                    results.Count(result => result.Succeeded),
                    results.Count(result => !result.Succeeded));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Plugin discovery failed.");
            lock (_sync)
            {
                _initialized = true;
            }
        }

        RaiseChanged();
    }

    /// <summary>Gets whether the initial scan has finished.</summary>
    public bool IsInitialized
    {
        get
        {
            lock (_sync)
            {
                return _initialized;
            }
        }
    }

    /// <summary>
    /// Disables a loaded plugin by shutting it down and removing its contributions.
    /// The plugin stays installed; restart DroidDeck to load it again.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin was found and disabled.</returns>
    public async Task<bool> DisableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var plugin = Results
            .Select(result => result.Plugin)
            .FirstOrDefault(candidate => candidate is not null &&
                string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            return false;
        }

        await plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _disabledPlugins.Add(plugin.Id);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Plugin {PluginId} was disabled. Its extensions were removed; a restart is required to load it again.",
                plugin.Id);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Builds a short health section for diagnostics reports.</summary>
    public string BuildHealthReport()
    {
        var builder = new StringBuilder();
        if (PluginsDisabled)
        {
            builder.AppendLine($"Plugins disabled for this launch: {DisabledReason}");
        }
        else if (!IsInitialized)
        {
            builder.AppendLine("Plugin scan has not completed.");
        }
        else
        {
            builder.AppendLine($"Loaded plugins: {LoadedCount}");
        }

        foreach (var result in Results)
        {
            if (result.Plugin is { } plugin)
            {
                var status = IsDisabled(plugin.Id) ? "disabled (restart to re-enable)" : "loaded";
                var health = _health.Get(plugin.Id);
                if (!health.IsHealthy)
                {
                    status += $", unhealthy: {health.LastError}";
                }

                builder.AppendLine($"- {plugin.Id} {plugin.Version}: {status}");
            }

            foreach (var issue in result.Validation.Issues)
            {
                builder.AppendLine($"- {result.Plugin?.Id ?? "unknown plugin"}: {issue}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _loader.DisposeAsync().ConfigureAwait(false);
    }

    private void RaiseChanged()
    {
        try
        {
            ResultsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A plugin status handler failed.");
        }
    }
}
