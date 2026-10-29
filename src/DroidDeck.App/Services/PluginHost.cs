using System.Text;
using DroidDeck.Core;
using DroidDeck.Plugins;
using DroidDeck.Plugins.BuiltIns;
using Microsoft.Extensions.Logging;

namespace DroidDeck.App.Services;

/// <summary>
/// Owns plugin discovery and lifecycle for the shell, extension page, and
/// diagnostics report. The manager loads plugins after the first frame and
/// isolates their faults; enable and disable complete on restart.
/// </summary>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly PluginManager _manager;
    private readonly AppPaths _paths;
    private readonly ILogger<PluginHost> _logger;
    private readonly object _sync = new();
    private readonly PluginExtensionCatalog _catalog;
    private readonly PluginHealthTracker _health;
    private readonly PluginActionInvoker _invoker;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a plugin host.</summary>
    public PluginHost(
        PluginRegistry registry,
        PluginLoader loader,
        AppPaths paths,
        ILogger<PluginHost> logger,
        ILoggerFactory? loggerFactory = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _health = new PluginHealthTracker();
        _invoker = new PluginActionInvoker(_health);
        _catalog = new PluginExtensionCatalog(registry);
        _manager = new PluginManager(loader, _health, loggerFactory?.CreateLogger<PluginManager>());
        _manager.Changed += OnManagerChanged;
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

    /// <summary>Gets whether a restart is required for plugin changes to take full effect.</summary>
    public bool RestartRequired => _manager.RestartRequired;

    /// <summary>Gets the manager status of every installed plugin.</summary>
    public IReadOnlyList<PluginStatus> Statuses => _manager.Statuses;

    /// <summary>Gets the results of the last plugin scan.</summary>
    public IReadOnlyList<PluginLoadResult> Results => _manager.Results;

    /// <summary>Gets the number of enabled, loaded plugins.</summary>
    public int LoadedCount => _manager.Statuses.Count(status => status.State == PluginState.Loaded);

    /// <summary>Gets whether a plugin is disabled for this session or on disk.</summary>
    public bool IsDisabled(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _manager.IsDisabled(pluginId);
    }

    /// <summary>Raised after a plugin scan or a manager action completes.</summary>
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
            lock (_sync)
            {
                _initialized = true;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Plugins disabled: {Reason}", disabledReason);
            }

            RaiseChanged();
            return;
        }

        try
        {
            await _manager.InitializeAsync(_paths.PluginsDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Plugin discovery failed.");
        }
        finally
        {
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
    /// Disables a plugin. Its extensions are removed immediately and the state
    /// persists; a restart is required to unload it.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin was found and disabled.</returns>
    public Task<bool> DisableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _manager.DisableAsync(pluginId, cancellationToken);
    }

    /// <summary>
    /// Enables a plugin. The plugin loads when DroidDeck restarts; the manager
    /// reports the restart requirement.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin was found and enabled.</returns>
    public Task<bool> EnableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _manager.EnableAsync(pluginId, cancellationToken);
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

        foreach (var status in _manager.Statuses)
        {
            var line = $"- {status.Id} {status.Version}: {status.StatusLabel}";
            if (status.Reason is not null)
            {
                line += $": {status.Reason}";
            }

            var health = _health.Get(status.Id);
            if (!health.IsHealthy)
            {
                line += $", unhealthy: {health.LastError}";
            }

            builder.AppendLine(line);
            if (status.Reason is null)
            {
                foreach (var issue in status.Issues)
                {
                    builder.AppendLine($"  - {issue}");
                }
            }
        }

        if (_manager.RestartRequired)
        {
            builder.AppendLine("A restart is required for plugin changes to take full effect.");
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
        _manager.Changed -= OnManagerChanged;
        await _manager.DisposeAsync().ConfigureAwait(false);
    }

    private void OnManagerChanged(object? sender, EventArgs e) => RaiseChanged();

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
