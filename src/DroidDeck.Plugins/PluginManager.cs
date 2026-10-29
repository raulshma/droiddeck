using Microsoft.Extensions.Logging;

namespace DroidDeck.Plugins;

/// <summary>
/// The manager-visible lifecycle state of an installed plugin.
/// </summary>
public enum PluginState
{
    /// <summary>The plugin is enabled and loaded.</summary>
    Loaded,

    /// <summary>The plugin is disabled by the user; a restart is required to unload or load it.</summary>
    Disabled,

    /// <summary>The user re-enabled the plugin; a restart is required to load it.</summary>
    EnabledPendingRestart,

    /// <summary>The plugin failed to load this launch; the manager shows the reason.</summary>
    DisabledByError,

    /// <summary>The plugin targets an incompatible API or host version.</summary>
    Incompatible,
}

/// <summary>One installed plugin as shown by the plugin manager.</summary>
public sealed record PluginStatus
{
    /// <summary>Gets the unique plugin identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the plugin version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the target API version.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets the plugin installation directory.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Gets the lifecycle state.</summary>
    public required PluginState State { get; init; }

    /// <summary>Gets the human-readable reason for the state, when one exists.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets the validation and load issues found for the plugin.</summary>
    public IReadOnlyList<PluginValidationIssue> Issues { get; init; } = [];

    /// <summary>Gets how long loading took; zero for plugins that did not load.</summary>
    public TimeSpan LoadDuration { get; init; }

    /// <summary>Gets whether the manager offers an enable action. Incompatible plugins cannot be enabled.</summary>
    public bool CanEnable => State is PluginState.Disabled or PluginState.DisabledByError;

    /// <summary>Gets whether the manager offers a disable action.</summary>
    public bool CanDisable => State is PluginState.Loaded or PluginState.EnabledPendingRestart;

    /// <summary>Gets the status label shown by the plugin manager.</summary>
    public string StatusLabel => State switch
    {
        PluginState.Loaded => "Enabled",
        PluginState.Disabled => "Disabled",
        PluginState.EnabledPendingRestart => "Enabled · restart required",
        PluginState.DisabledByError => "Disabled by error",
        _ => "Incompatible",
    };
}

/// <summary>
/// Orchestrates the plugin lifecycle for the shell: scans installed plugins
/// without loading them, loads the enabled ones, marks load failures unhealthy,
/// and applies enable and disable transitions. Disable removes a plugin's
/// extensions immediately; unloading it requires a restart, and the manager
/// says so through <see cref="RestartRequired"/>.
/// </summary>
public sealed class PluginManager : IAsyncDisposable
{
    private readonly PluginLoader _loader;
    private readonly PluginHealthTracker _health;
    private readonly ILogger<PluginManager>? _logger;
    private readonly object _sync = new();
    private List<PluginStatus> _statuses = [];
    private IReadOnlyList<PluginLoadResult> _results = [];
    private PluginStateStore? _store;
    private bool _restartRequired;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a manager over a loader and the shared health tracker.</summary>
    public PluginManager(PluginLoader loader, PluginHealthTracker health, ILogger<PluginManager>? logger = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _logger = logger;
    }

    /// <summary>Raised after statuses or state changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the statuses of every installed plugin.</summary>
    public IReadOnlyList<PluginStatus> Statuses
    {
        get
        {
            lock (_sync)
            {
                return _statuses.ToArray();
            }
        }
    }

    /// <summary>Gets the results of the last launch's plugin scan.</summary>
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

    /// <summary>Gets whether an enable or disable is waiting to complete on restart.</summary>
    public bool RestartRequired
    {
        get
        {
            lock (_sync)
            {
                return _restartRequired;
            }
        }
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

    /// <summary>Returns whether a plugin is disabled for this session or on disk.</summary>
    public bool IsDisabled(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _store?.IsDisabled(pluginId) ?? false;
        }
    }

    /// <summary>
    /// Scans the installed plugins, loads the enabled ones, and builds manager
    /// statuses. Plugin manifest metadata is read without loading assemblies.
    /// </summary>
    public async Task InitializeAsync(string pluginsRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var store = await PluginStateStore.LoadAsync(pluginsRoot, cancellationToken).ConfigureAwait(false);
        var disabled = new HashSet<string>(store.DisabledIds, StringComparer.OrdinalIgnoreCase);
        var manifests = await ScanAsync(pluginsRoot, cancellationToken).ConfigureAwait(false);
        var results = await _loader
            .LoadInstalledAsync(pluginsRoot, disabled, cancellationToken)
            .ConfigureAwait(false);

        var failures = new List<(string Id, string Reason)>();
        var statuses = BuildStatuses(manifests, results, disabled, failures);

        lock (_sync)
        {
            _store = store;
            _results = results;
            _statuses = statuses;
            _initialized = true;
        }

        foreach (var (id, reason) in failures)
        {
            _health.RecordFailure(id, reason);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Enables a plugin. The plugin is not hot-loaded; it loads when DroidDeck
    /// restarts, which the manager signals with <see cref="RestartRequired"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin was found and enabled.</returns>
    public async Task<bool> EnableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var store = _store;
        var status = FindStatus(pluginId);
        if (store is null || status is null || !status.CanEnable)
        {
            return false;
        }

        try
        {
            await store.SetDisabledAsync(pluginId, disabled: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The store already updated its in-memory state, and its next
            // successful write persists the full set, so only the log is needed.
            _logger?.LogWarning(
                exception,
                "The plugin state for {PluginId} could not be persisted; the change applies to this session until the next successful save.",
                pluginId);
        }

        ReplaceStatus(status with
        {
            State = PluginState.EnabledPendingRestart,
            Reason = status.State == PluginState.Disabled ? "Enabled. Restart DroidDeck to load it." : status.Reason,
        });
        SetRestartRequired();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Disables a plugin. Its extensions are removed immediately, its assemblies
    /// stay loaded until restart, and the disabled state persists for the next
    /// launch.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin was found and disabled.</returns>
    public async Task<bool> DisableAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var store = _store;
        var status = FindStatus(pluginId);
        if (store is null || status is null || !status.CanDisable)
        {
            return false;
        }

        var loaded = _loader.Plugins.FirstOrDefault(
            candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        try
        {
            await store.SetDisabledAsync(pluginId, disabled: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The store already updated its in-memory state, and its next
            // successful write persists the full set, so only the log is needed.
            _logger?.LogWarning(
                exception,
                "The plugin state for {PluginId} could not be persisted; the change applies to this session until the next successful save.",
                pluginId);
        }

        loaded?.Disable();
        ReplaceStatus(status with
        {
            State = PluginState.Disabled,
            Reason = loaded is not null
                ? "Disabled. Its extensions were removed; restart DroidDeck to unload it completely."
                : "Disabled. Enable it and restart DroidDeck to load it.",
        });
        SetRestartRequired();
        RaiseChanged();
        return true;
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

    private PluginStatus? FindStatus(string pluginId)
    {
        lock (_sync)
        {
            return _statuses.FirstOrDefault(
                candidate => string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void ReplaceStatus(PluginStatus replacement)
    {
        lock (_sync)
        {
            var index = _statuses.FindIndex(
                candidate => string.Equals(candidate.Id, replacement.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _statuses[index] = replacement;
            }
        }
    }

    private void SetRestartRequired()
    {
        lock (_sync)
        {
            _restartRequired = true;
        }
    }

    private static async Task<List<(string Directory, PluginManifestReadResult Read)>> ScanAsync(
        string pluginsRoot,
        CancellationToken cancellationToken)
    {
        var scans = new List<(string, PluginManifestReadResult)>();
        if (!Directory.Exists(pluginsRoot))
        {
            return scans;
        }

        var directories = Directory
            .EnumerateDirectories(pluginsRoot)
            .Where(directory => File.Exists(PluginManifestReader.GetManifestPath(directory)))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scans.Add((directory, await PluginManifestReader.ReadAsync(directory, cancellationToken).ConfigureAwait(false)));
        }

        return scans;
    }

    private static List<PluginStatus> BuildStatuses(
        List<(string Directory, PluginManifestReadResult Read)> manifests,
        IReadOnlyList<PluginLoadResult> results,
        HashSet<string> disabled,
        List<(string Id, string Reason)> failures)
    {
        var statuses = new List<PluginStatus>();
        var byId = results
            .Where(result => result.Manifest is not null)
            .GroupBy(
                result => result.Manifest!.Id,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var (directory, read) in manifests)
        {
            var manifest = read.Manifest;
            if (manifest is null)
            {
                var message = read.Validation.Issues.LastOrDefault(
                    issue => issue.Severity == PluginValidationSeverity.Error)?.Message
                    ?? "The plugin manifest could not be read.";
                failures.Add((Path.GetFileName(directory), message));
                statuses.Add(new PluginStatus
                {
                    Id = Path.GetFileName(directory),
                    Name = "Unknown plugin",
                    Version = string.Empty,
                    ApiVersion = string.Empty,
                    InstallPath = directory,
                    State = PluginState.DisabledByError,
                    Reason = message,
                    Issues = read.Validation.Issues,
                });
                continue;
            }

            if (disabled.Contains(manifest.Id))
            {
                statuses.Add(new PluginStatus
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    ApiVersion = manifest.ApiVersion,
                    InstallPath = directory,
                    State = PluginState.Disabled,
                    Reason = "Disabled. Enable it and restart DroidDeck to load it.",
                    Issues = read.Validation.Issues,
                });
                continue;
            }

            if (!byId.TryGetValue(manifest.Id, out var result))
            {
                const string missingResult = "The plugin was not loaded this launch.";
                failures.Add((manifest.Id, missingResult));
                statuses.Add(new PluginStatus
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    ApiVersion = manifest.ApiVersion,
                    InstallPath = directory,
                    State = PluginState.DisabledByError,
                    Reason = missingResult,
                    Issues = read.Validation.Issues,
                });
                continue;
            }

            var issues = result.Validation.Issues;
            if (result.Succeeded)
            {
                statuses.Add(new PluginStatus
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    ApiVersion = manifest.ApiVersion,
                    InstallPath = directory,
                    State = PluginState.Loaded,
                    Issues = issues,
                    LoadDuration = result.LoadDuration,
                });
                continue;
            }

            var reason = issues.LastOrDefault(issue => issue.Severity == PluginValidationSeverity.Error)?.Message
                ?? "The plugin could not be loaded.";
            failures.Add((manifest.Id, reason));
            statuses.Add(new PluginStatus
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                ApiVersion = manifest.ApiVersion,
                InstallPath = directory,
                State = ClassifyFailure(result.Validation),
                Reason = reason,
                Issues = issues,
            });
        }

        return statuses;
    }

    private static PluginState ClassifyFailure(PluginValidationResult validation)
    {
        foreach (var issue in validation.Issues)
        {
            if (issue.Severity != PluginValidationSeverity.Error)
            {
                continue;
            }

            if (issue.Code.StartsWith("manifest.apiVersion.", StringComparison.Ordinal) ||
                issue.Code.StartsWith("manifest.minHostVersion.", StringComparison.Ordinal) ||
                issue.Code.StartsWith("manifest.maxHostVersion.", StringComparison.Ordinal))
            {
                return PluginState.Incompatible;
            }
        }

        return PluginState.DisabledByError;
    }

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
