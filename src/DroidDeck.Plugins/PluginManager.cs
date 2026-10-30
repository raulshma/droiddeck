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

    /// <summary>The plugin contents were not approved yet or changed after approval.</summary>
    TrustRequired,
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

    /// <summary>Gets the declared publisher, when one exists.</summary>
    public string? Author { get; init; }

    /// <summary>Gets the capability names declared by the plugin manifest.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Gets the plugin installation directory.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Gets the lifecycle state.</summary>
    public required PluginState State { get; init; }

    /// <summary>Gets the trust decision for the plugin contents.</summary>
    public PluginTrustDecision Trust { get; init; } = PluginTrustDecision.Trusted;

    /// <summary>Gets the hash of the current plugin contents, when it was computed.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Gets the hash that was approved for the plugin, when one exists.</summary>
    public string? ApprovedHash { get; init; }

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

    /// <summary>Gets whether the manager offers a trust action.</summary>
    public bool CanTrust => State == PluginState.TrustRequired;

    /// <summary>Gets the status label shown by the plugin manager.</summary>
    public string StatusLabel => State switch
    {
        PluginState.Loaded => "Enabled",
        PluginState.Disabled => "Disabled",
        PluginState.EnabledPendingRestart => "Enabled · restart required",
        PluginState.DisabledByError => "Disabled by error",
        PluginState.TrustRequired => "Approval required",
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
    private readonly PluginTrustPolicy _trustPolicy;
    private readonly object _sync = new();
    private List<PluginStatus> _statuses = [];
    private IReadOnlyList<PluginLoadResult> _results = [];
    private PluginStateStore? _store;
    private PluginTrustStore? _trust;
    private bool _restartRequired;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a manager over a loader and the shared health tracker.</summary>
    public PluginManager(
        PluginLoader loader,
        PluginHealthTracker health,
        ILogger<PluginManager>? logger = null,
        PluginTrustPolicy trustPolicy = PluginTrustPolicy.RequireApproval)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _logger = logger;
        _trustPolicy = trustPolicy;
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
        var trust = _trustPolicy == PluginTrustPolicy.RequireApproval
            ? await PluginTrustStore.LoadAsync(pluginsRoot, cancellationToken).ConfigureAwait(false)
            : null;
        var manifests = await ScanAsync(pluginsRoot, cancellationToken).ConfigureAwait(false);
        var evaluations = await EvaluateTrustAsync(manifests, disabled, trust, cancellationToken)
            .ConfigureAwait(false);
        var skip = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in evaluations)
        {
            if (pair.Value.Decision != PluginTrustDecision.Trusted)
            {
                skip.Add(pair.Key);
            }
        }

        var results = await _loader
            .LoadInstalledAsync(pluginsRoot, skip, cancellationToken)
            .ConfigureAwait(false);

        var failures = new List<(string Id, string Reason)>();
        var statuses = BuildStatuses(manifests, results, disabled, store, evaluations, failures);

        lock (_sync)
        {
            _store = store;
            _trust = trust;
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
            await store.SetDisabledAsync(pluginId, disabled: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
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
    /// <param name="pluginId">The plugin to disable.</param>
    /// <param name="reason">Overrides the status reason, for example after repeated crashes.</param>
    /// <param name="cancellationToken">Cancels the state write.</param>
    /// <returns><see langword="true"/> when the plugin was found and disabled.</returns>
    public async Task<bool> DisableAsync(
        string pluginId,
        string? reason = null,
        CancellationToken cancellationToken = default)
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
            await store.SetDisabledAsync(pluginId, disabled: true, reason, cancellationToken)
                .ConfigureAwait(false);
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
            Reason = reason ?? (loaded is not null
                ? "Disabled. Its extensions were removed; restart DroidDeck to unload it completely."
                : "Disabled. Enable it and restart DroidDeck to load it."),
        });
        SetRestartRequired();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Approves a plugin whose contents are unknown or changed. The current
    /// contents are pinned, and the plugin loads after DroidDeck restarts.
    /// </summary>
    /// <returns><see langword="true"/> when the plugin needed approval and it was recorded.</returns>
    public async Task<bool> TrustAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var trust = _trust;
        var status = FindStatus(pluginId);
        if (trust is null ||
            status is not { State: PluginState.TrustRequired, ContentHash: not null })
        {
            return false;
        }

        try
        {
            await trust.ApproveAsync(
                status.Id,
                status.ContentHash,
                status.Name,
                status.Version,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Approval only counts when the pinned hash is durable.
            _logger?.LogWarning(
                exception,
                "The trust decision for {PluginId} could not be persisted.",
                pluginId);
            return false;
        }

        ReplaceStatus(status with
        {
            State = PluginState.EnabledPendingRestart,
            Trust = PluginTrustDecision.Trusted,
            ApprovedHash = status.ContentHash,
            Reason = "Approved. Restart DroidDeck to load it.",
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
        PluginStateStore? store,
        IReadOnlyDictionary<string, TrustEvaluation> evaluations,
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

            evaluations.TryGetValue(manifest.Id, out var evaluation);
            if (disabled.Contains(manifest.Id))
            {
                statuses.Add(new PluginStatus
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    ApiVersion = manifest.ApiVersion,
                    Author = manifest.Author,
                    Capabilities = manifest.Capabilities,
                    InstallPath = directory,
                    State = PluginState.Disabled,
                    Reason = store?.GetDisabledReason(manifest.Id) ??
                        "Disabled. Enable it and restart DroidDeck to load it.",
                    Issues = read.Validation.Issues,
                    Trust = evaluation?.Decision ?? PluginTrustDecision.Trusted,
                    ContentHash = evaluation?.ContentHash,
                    ApprovedHash = evaluation?.ApprovedHash,
                });
                continue;
            }

            if (evaluation is not null && evaluation.Decision != PluginTrustDecision.Trusted)
            {
                statuses.Add(new PluginStatus
                {
                    Id = manifest.Id,
                    Name = manifest.Name,
                    Version = manifest.Version,
                    ApiVersion = manifest.ApiVersion,
                    Author = manifest.Author,
                    Capabilities = manifest.Capabilities,
                    InstallPath = directory,
                    State = PluginState.TrustRequired,
                    Reason = BuildTrustReason(evaluation),
                    Issues = read.Validation.Issues,
                    Trust = evaluation.Decision,
                    ContentHash = evaluation.ContentHash,
                    ApprovedHash = evaluation.ApprovedHash,
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
                    Author = manifest.Author,
                    Capabilities = manifest.Capabilities,
                    InstallPath = directory,
                    State = PluginState.DisabledByError,
                    Reason = missingResult,
                    Issues = read.Validation.Issues,
                    ContentHash = evaluation?.ContentHash,
                    ApprovedHash = evaluation?.ApprovedHash,
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
                    Author = manifest.Author,
                    Capabilities = manifest.Capabilities,
                    InstallPath = directory,
                    State = PluginState.Loaded,
                    Issues = issues,
                    LoadDuration = result.LoadDuration,
                    ContentHash = evaluation?.ContentHash,
                    ApprovedHash = evaluation?.ApprovedHash,
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
                Author = manifest.Author,
                Capabilities = manifest.Capabilities,
                InstallPath = directory,
                State = ClassifyFailure(result.Validation),
                Reason = reason,
                Issues = issues,
                ContentHash = evaluation?.ContentHash,
                ApprovedHash = evaluation?.ApprovedHash,
            });
        }

        return statuses;
    }

    /// <summary>
    /// Computes the content hash for every enabled plugin and compares it with
    /// the approved hash. Hashing failures fail closed: the plugin is reported as
    /// untrusted until its files can be read.
    /// </summary>
    private static async Task<Dictionary<string, TrustEvaluation>> EvaluateTrustAsync(
        List<(string Directory, PluginManifestReadResult Read)> manifests,
        HashSet<string> disabled,
        PluginTrustStore? trust,
        CancellationToken cancellationToken)
    {
        var evaluations = new Dictionary<string, TrustEvaluation>(StringComparer.OrdinalIgnoreCase);
        if (trust is null)
        {
            return evaluations;
        }

        foreach (var (directory, read) in manifests)
        {
            var manifest = read.Manifest;
            if (manifest is null || disabled.Contains(manifest.Id))
            {
                continue;
            }

            var approved = trust.Find(manifest.Id);
            try
            {
                var hash = await PluginContentHasher.ComputeAsync(directory, cancellationToken)
                    .ConfigureAwait(false);
                evaluations[manifest.Id] = new TrustEvaluation(
                    trust.Evaluate(manifest.Id, hash),
                    hash,
                    approved?.ContentHash,
                    approved?.ApprovedAt,
                    Error: null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                evaluations[manifest.Id] = new TrustEvaluation(
                    PluginTrustDecision.Unknown,
                    ContentHash: null,
                    approved?.ContentHash,
                    approved?.ApprovedAt,
                    exception.Message);
            }
        }

        return evaluations;
    }

    private static string BuildTrustReason(TrustEvaluation evaluation)
    {
        if (evaluation.Error is not null)
        {
            return $"The plugin contents could not be verified: {evaluation.Error}. " +
                "It will not load until its files can be read and approved.";
        }

        if (evaluation.Decision == PluginTrustDecision.Unknown)
        {
            return "This plugin has not been approved on this machine. " +
                "Review its files and choose Trust before DroidDeck loads it.";
        }

        return "The plugin contents changed since they were approved on " +
            $"{evaluation.ApprovedAt?.ToLocalTime():g} " +
            $"(approved {ShortHash(evaluation.ApprovedHash)}, current {ShortHash(evaluation.ContentHash)}). " +
            "Review the new version and choose Trust again.";
    }

    private static string ShortHash(string? hash) =>
        string.IsNullOrEmpty(hash)
            ? "unknown"
            : hash.Length <= 12 ? hash : hash[..12] + "…";

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

    private sealed record TrustEvaluation(
        PluginTrustDecision Decision,
        string? ContentHash,
        string? ApprovedHash,
        DateTimeOffset? ApprovedAt,
        string? Error);

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
