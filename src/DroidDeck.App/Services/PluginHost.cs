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
    }

    /// <summary>Gets the shared extension registry.</summary>
    public PluginRegistry Registry => _registry;

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
    public int LoadedCount => Results.Count(result => result.Succeeded);

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
                builder.AppendLine($"- {plugin.Id} {plugin.Version}: loaded");
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
