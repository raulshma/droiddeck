using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// Resolves the eight configuration scopes in precedence order (built-in defaults,
/// global, active profile, team-shared project, local project, run configuration,
/// device, and session), tracks where every effective value came from, and persists
/// the writable layers atomically.
/// </summary>
public sealed class ConfigurationService : IConfigurationService, IDisposable
{
    private readonly AppPaths _paths;
    private readonly ILogger<ConfigurationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SettingsMigrator _migrator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<WeakReference<ConfigChangeToken>> _tokens = [];
    private JsonObject _global = new();
    private JsonObject _profile = new();
    private JsonObject _teamProject = new();
    private JsonObject _localProject = new();
    private JsonObject _runConfiguration = new();
    private JsonObject _deviceAll = new();
    private Dictionary<string, JsonObject> _deviceGroups = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, JsonObject> _deviceSerials = new(StringComparer.OrdinalIgnoreCase);
    private JsonObject _session = new();
    private string? _teamRepositoryRoot;
    private string? _localProjectId;
    private string? _loadedProfileId;
    private volatile ConfigSnapshot _current;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a configuration service for the provided data paths.</summary>
    public ConfigurationService(
        AppPaths paths,
        ILogger<ConfigurationService>? logger = null,
        TimeProvider? timeProvider = null,
        SettingsMigrator? migrator = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<ConfigurationService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _migrator = migrator ?? SettingsMigrator.Default;
        _current = BuildSnapshot();
    }

    /// <inheritdoc />
    public ConfigSnapshot Current => _current;

    /// <inheritdoc />
    public bool IsInitialized => _initialized;

    /// <inheritdoc />
    public SettingsLoadNotice? LastLoadNotice { get; private set; }

    /// <inheritdoc />
    public event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(
        string path,
        T value,
        ConfigScope scope = ConfigScope.Global,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        ValidateWritableScope(scope);
        var node = JsonSerializer.SerializeToNode(value, DroidDeckJson.Options)?.DeepClone();

        ConfigChangedEventArgs? change = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            switch (scope)
            {
                case ConfigScope.Global:
                    var globalCandidate = (JsonObject)_global.DeepClone();
                    SetNode(globalCandidate, path, node);
                    AddSchemaVersion(globalCandidate);
                    await SaveGlobalLockedAsync(globalCandidate, cancellationToken).ConfigureAwait(false);
                    break;
                case ConfigScope.TeamProject:
                    RequireTeamProjectLocked();
                    var teamCandidate = (JsonObject)_teamProject.DeepClone();
                    SetNode(teamCandidate, path, node);
                    AddSchemaVersion(teamCandidate);
                    await SaveTeamProjectLockedAsync(teamCandidate, cancellationToken).ConfigureAwait(false);
                    break;
                case ConfigScope.LocalProject:
                    RequireLocalProjectLocked();
                    var localCandidate = (JsonObject)_localProject.DeepClone();
                    SetNode(localCandidate, path, node);
                    AddSchemaVersion(localCandidate);
                    await SaveLocalProjectLockedAsync(localCandidate, cancellationToken).ConfigureAwait(false);
                    break;
                case ConfigScope.RunConfiguration:
                    var runCandidate = (JsonObject)_runConfiguration.DeepClone();
                    SetNode(runCandidate, path, node);
                    _runConfiguration = runCandidate;
                    break;
                case ConfigScope.Session:
                    var sessionCandidate = (JsonObject)_session.DeepClone();
                    SetNode(sessionCandidate, path, node);
                    _session = sessionCandidate;
                    break;
            }

            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
            change = new ConfigChangedEventArgs(path, scope, _current.GetSource(path), ConfigChangeKind.Set);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, change);
    }

    /// <inheritdoc />
    public async Task ResetAsync(
        string path,
        ConfigScope scope = ConfigScope.Global,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        if (scope is ConfigScope.BuiltInDefaults or ConfigScope.Device)
        {
            throw new NotSupportedException(
                scope == ConfigScope.BuiltInDefaults
                    ? "Built-in defaults cannot be reset."
                    : "Use ResetDeviceOverrideAsync for device and device group overrides.");
        }

        ConfigChangedEventArgs? change = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            switch (scope)
            {
                case ConfigScope.Global:
                    var globalCandidate = (JsonObject)_global.DeepClone();
                    if (RemoveNode(globalCandidate, path))
                    {
                        AddSchemaVersion(globalCandidate);
                        await SaveGlobalLockedAsync(globalCandidate, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case ConfigScope.TeamProject:
                    RequireTeamProjectLocked();
                    var teamCandidate = (JsonObject)_teamProject.DeepClone();
                    if (RemoveNode(teamCandidate, path))
                    {
                        AddSchemaVersion(teamCandidate);
                        await SaveTeamProjectLockedAsync(teamCandidate, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case ConfigScope.LocalProject:
                    RequireLocalProjectLocked();
                    var localCandidate = (JsonObject)_localProject.DeepClone();
                    if (RemoveNode(localCandidate, path))
                    {
                        AddSchemaVersion(localCandidate);
                        await SaveLocalProjectLockedAsync(localCandidate, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case ConfigScope.RunConfiguration:
                    var runCandidate = (JsonObject)_runConfiguration.DeepClone();
                    RemoveNode(runCandidate, path);
                    _runConfiguration = runCandidate;
                    break;
                case ConfigScope.Profile:
                    var profileCandidate = (JsonObject)_profile.DeepClone();
                    RemoveNode(profileCandidate, path);
                    _profile = profileCandidate;
                    break;
                case ConfigScope.Session:
                    var sessionCandidate = (JsonObject)_session.DeepClone();
                    RemoveNode(sessionCandidate, path);
                    _session = sessionCandidate;
                    break;
            }

            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
            change = new ConfigChangedEventArgs(path, scope, _current.GetSource(path), ConfigChangeKind.Reset);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, change);
    }

    /// <inheritdoc />
    public async Task SetProjectContextAsync(ProjectContext? context, CancellationToken cancellationToken = default)
    {
        string? repositoryRoot = null;
        string? localProjectId = null;
        if (context is not null)
        {
            if (!string.IsNullOrWhiteSpace(context.RepositoryRoot))
            {
                repositoryRoot = Path.GetFullPath(context.RepositoryRoot);
            }

            if (!string.IsNullOrWhiteSpace(context.LocalProjectId))
            {
                ValidateIdentifier(context.LocalProjectId);
                localProjectId = context.LocalProjectId;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _teamRepositoryRoot = repositoryRoot;
            _localProjectId = localProjectId;
            _teamProject = repositoryRoot is null
                ? []
                : await LoadLayerDocumentAsync(
                    TeamProjectFile,
                    layerName: "team-shared project",
                    quarantineOnFailure: false,
                    cancellationToken).ConfigureAwait(false) ?? [];
            _localProject = localProjectId is null
                ? []
                : await LoadLayerDocumentAsync(
                    LocalProjectFile,
                    layerName: "local project",
                    quarantineOnFailure: true,
                    cancellationToken).ConfigureAwait(false) ?? [];
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task UseProfileAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        if (profileId is not null)
        {
            ValidateIdentifier(profileId);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            var candidate = (JsonObject)_global.DeepClone();
            if (profileId is null)
            {
                RemoveNode(candidate, SettingsPaths.ActiveProfileId);
            }
            else
            {
                SetNode(candidate, SettingsPaths.ActiveProfileId, JsonValue.Create(profileId));
            }

            if (!JsonNode.DeepEquals(candidate, _global))
            {
                AddSchemaVersion(candidate);
                await SaveGlobalLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
            }

            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(
            this,
            new ConfigChangedEventArgs(
                SettingsPaths.ActiveProfileId,
                ConfigScope.Global,
                _current.GetSource(SettingsPaths.ActiveProfileId),
                profileId is null ? ConfigChangeKind.Reset : ConfigChangeKind.Set));
    }

    /// <inheritdoc />
    public async Task SetRunConfigurationAsync(
        RunConfiguration? configuration,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            _runConfiguration = configuration is null
                ? []
                : RunConfigurationOverrides.Project(configuration);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetSessionOverridesAsync(
        IEnumerable<KeyValuePair<string, string>> overrides,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var pairs = overrides.ToList();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var candidate = (JsonObject)_session.DeepClone();
            foreach (var pair in pairs)
            {
                ValidatePath(pair.Key);
                SetNode(candidate, pair.Key, CliOverrides.ParseValue(pair.Value));
            }

            _session = candidate;
            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var pair in pairs)
        {
            Changed?.Invoke(
                this,
                new ConfigChangedEventArgs(
                    pair.Key,
                    ConfigScope.Session,
                    _current.GetSource(pair.Key),
                    ConfigChangeKind.Set));
        }
    }

    /// <inheritdoc />
    public async Task ClearSessionOverridesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_session.Count == 0)
            {
                return;
            }

            _session = new JsonObject();
            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetDeviceOverrideAsync<T>(
        string path,
        T value,
        DeviceOverrideTarget target,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        ValidateTarget(target);
        var node = JsonSerializer.SerializeToNode(value, DroidDeckJson.Options)?.DeepClone();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var candidate = (JsonObject)DeviceDocumentLocked(target).DeepClone();
            SetNode(candidate, path, node);
            StoreDeviceDocumentLocked(target, candidate);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(
            this,
            new ConfigChangedEventArgs(
                path,
                ConfigScope.Device,
                _current.GetSource(path),
                ConfigChangeKind.Set));
    }

    /// <inheritdoc />
    public async Task ResetDeviceOverrideAsync(
        string path,
        DeviceOverrideTarget target,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(path);
        ValidateTarget(target);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var candidate = (JsonObject)DeviceDocumentLocked(target).DeepClone();
            RemoveNode(candidate, path);
            StoreDeviceDocumentLocked(target, candidate);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(
            this,
            new ConfigChangedEventArgs(
                path,
                ConfigScope.Device,
                _current.GetSource(path),
                ConfigChangeKind.Reset));
    }

    /// <inheritdoc />
    public ConfigSource GetSource(string path) => _current.GetSource(path);

    /// <inheritdoc />
    public T? GetValue<T>(string path)
    {
        var snapshot = _current;
        if (snapshot.TryGetCached<T>(path, out var cached))
        {
            return cached;
        }

        T? value = default;
        var node = snapshot.GetValue(path);
        if (node is not null)
        {
            try
            {
                value = node.Deserialize<T>(DroidDeckJson.Options);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "The setting {Path} could not be read as {Type}.", path, typeof(T).Name);
            }
        }

        snapshot.CacheValue(path, value);
        return value;
    }

    /// <inheritdoc />
    public string? GetString(string path)
    {
        var node = _current.GetValue(path);
        if (node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    }

    /// <inheritdoc />
    public JsonNode? GetValueAt(ConfigScope scope, string path)
    {
        ValidatePath(path);
        var document = scope switch
        {
            ConfigScope.Global => _global,
            ConfigScope.Profile => _loadedProfileId is null ? null : _profile,
            ConfigScope.TeamProject => _teamRepositoryRoot is null ? null : _teamProject,
            ConfigScope.LocalProject => _localProjectId is null ? null : _localProject,
            ConfigScope.RunConfiguration => _runConfiguration,
            ConfigScope.Device => _deviceAll,
            ConfigScope.Session => _session,
            _ => null,
        };

        return document is null ? null : GetNode(document, path);
    }

    /// <inheritdoc />
    public SettingsValidationResult ValidateSettings(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var issues = new List<SettingsValidationIssue>();
        JsonObject document;
        try
        {
            document = ParseDocument(json);
        }
        catch (JsonException exception)
        {
            issues.Add(new SettingsValidationIssue(
                SettingsTransfer.NormalizeJsonPath(exception.Path),
                $"Invalid JSON: {exception.Message}",
                SettingsIssueSeverity.Error));
            return new SettingsValidationResult(issues);
        }
        catch (InvalidDataException exception)
        {
            issues.Add(new SettingsValidationIssue(string.Empty, exception.Message, SettingsIssueSeverity.Error));
            return new SettingsValidationResult(issues);
        }

        ValidateDocument(document, issues);
        return new SettingsValidationResult(issues);
    }

    /// <inheritdoc />
    public async Task<SettingsExport> ExportSettingsAsync(
        SettingsExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var excluded = new List<string>();
            var scopes = new Dictionary<ConfigScope, JsonObject>();
            foreach (var scope in options?.Scopes ?? SettingsExport.DefaultScopes)
            {
                var document = ScopeDocumentLocked(scope);
                if (document is null)
                {
                    continue;
                }

                var copy = SettingsTransfer.Sanitize(document, excluded);
                copy["schemaVersion"] = AppSettings.CurrentSchemaVersion;
                scopes[scope] = copy;
            }

            return new SettingsExport
            {
                GeneratedAt = _timeProvider.GetUtcNow(),
                Scopes = scopes,
                ExcludedPaths = excluded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SettingsImportPreview> PreviewSettingsImportAsync(
        string json,
        ConfigScope scope = ConfigScope.Global,
        SettingsImportMode mode = SettingsImportMode.Merge,
        CancellationToken cancellationToken = default)
    {
        ValidateImportScope(scope);
        var prepared = PrepareImport(json, scope);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var current = ScopeDocumentLocked(scope) ?? new JsonObject();
            IReadOnlyList<SettingsChange> changes = prepared.Validation.IsValid
                ? SettingsTransfer.Diff(
                    SettingsTransfer.Flatten(current),
                    SettingsTransfer.Flatten(prepared.Document),
                    mode)
                : [];
            return new SettingsImportPreview
            {
                Scope = scope,
                Mode = mode,
                Validation = prepared.Validation,
                Changes = changes,
                ExcludedPaths = prepared.Excluded,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SettingsImportResult> ImportSettingsAsync(
        string json,
        ConfigScope scope = ConfigScope.Global,
        SettingsImportMode mode = SettingsImportMode.Merge,
        CancellationToken cancellationToken = default)
    {
        ValidateImportScope(scope);
        var prepared = PrepareImport(json, scope);
        if (!prepared.Validation.IsValid)
        {
            throw new SettingsValidationException(prepared.Validation);
        }

        SettingsImportPreview preview;
        string? backupPath;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            RequireWritableLayerLocked(scope);
            var current = ScopeDocumentLocked(scope) ?? new JsonObject();
            preview = new SettingsImportPreview
            {
                Scope = scope,
                Mode = mode,
                Validation = prepared.Validation,
                Changes = SettingsTransfer.Diff(
                    SettingsTransfer.Flatten(current),
                    SettingsTransfer.Flatten(prepared.Document),
                    mode),
                ExcludedPaths = prepared.Excluded,
            };

            var candidate = mode == SettingsImportMode.Replace
                ? (JsonObject)prepared.Document.DeepClone()
                : (JsonObject)current.DeepClone();
            if (mode == SettingsImportMode.Merge)
            {
                SettingsTransfer.Merge(candidate, prepared.Document);
            }

            SettingsTransfer.PreserveSensitive(current, candidate);
            AddSchemaVersion(candidate);
            backupPath = await SaveLayerLockedAsync(scope, candidate, cancellationToken).ConfigureAwait(false);
            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var change in preview.Changes)
        {
            Changed?.Invoke(
                this,
                new ConfigChangedEventArgs(
                    change.Path,
                    scope,
                    _current.GetSource(change.Path),
                    change.Kind == SettingsChangeKind.Removed ? ConfigChangeKind.Reset : ConfigChangeKind.Set));
        }

        return new SettingsImportResult(preview, backupPath);
    }

    /// <inheritdoc />
    public async Task<SettingsResetSnapshot> ResetSectionAsync(
        string section,
        ConfigScope scope = ConfigScope.Global,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        if (section.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("A section name cannot contain dots.", nameof(section));
        }

        ValidateImportScope(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            RequireWritableLayerLocked(scope);
            var current = ScopeDocumentLocked(scope) ?? new JsonObject();
            var key = SettingsTransfer.FindKey(current, section);
            var removed = SettingsTransfer.Flatten(current)
                .Where(pair =>
                    pair.Key.Equals(section, StringComparison.OrdinalIgnoreCase) ||
                    pair.Key.StartsWith(section + ".", StringComparison.OrdinalIgnoreCase))
                .Select(pair => new SettingsChange(pair.Key, SettingsChangeKind.Removed, pair.Value, null))
                .OrderBy(static change => change.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (key is not null)
            {
                var candidate = (JsonObject)current.DeepClone();
                candidate.Remove(key);
                AddSchemaVersion(candidate);
                await SaveLayerLockedAsync(scope, candidate, cancellationToken).ConfigureAwait(false);
                await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
                _current = BuildSnapshot();
                NotifyTokensLocked();
            }

            return new SettingsResetSnapshot
            {
                Scope = scope,
                Document = (JsonObject)current.DeepClone(),
                Removed = removed,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SettingsResetSnapshot> ResetScopeAsync(
        ConfigScope scope = ConfigScope.Global,
        CancellationToken cancellationToken = default)
    {
        ValidateImportScope(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            RequireWritableLayerLocked(scope);
            var current = ScopeDocumentLocked(scope) ?? new JsonObject();
            var removed = SettingsTransfer.Flatten(current)
                .Where(static pair => !pair.Key.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase))
                .Select(pair => new SettingsChange(pair.Key, SettingsChangeKind.Removed, pair.Value, null))
                .OrderBy(static change => change.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var candidate = new JsonObject();
            AddSchemaVersion(candidate);
            await SaveLayerLockedAsync(scope, candidate, cancellationToken).ConfigureAwait(false);
            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();

            return new SettingsResetSnapshot
            {
                Scope = scope,
                Document = (JsonObject)current.DeepClone(),
                Removed = removed,
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RestoreSettingsAsync(
        SettingsResetSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateImportScope(snapshot.Scope);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            RequireWritableLayerLocked(snapshot.Scope);
            var issues = new List<SettingsValidationIssue>();
            ValidateDocument(snapshot.Document, issues);
            if (issues.Any(static issue => issue.Severity == SettingsIssueSeverity.Error))
            {
                throw new SettingsValidationException(new SettingsValidationResult(issues));
            }

            var candidate = (JsonObject)snapshot.Document.DeepClone();
            AddSchemaVersion(candidate);
            await SaveLayerLockedAsync(snapshot.Scope, candidate, cancellationToken).ConfigureAwait(false);
            await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
            _current = BuildSnapshot();
            NotifyTokensLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ConfigChangeToken GetChangeToken()
    {
        lock (_tokens)
        {
            var token = new ConfigChangeToken();
            _tokens.Add(new WeakReference<ConfigChangeToken>(token));
            return token;
        }
    }

    private void NotifyTokensLocked()
    {
        lock (_tokens)
        {
            for (var index = _tokens.Count - 1; index >= 0; index--)
            {
                if (!_tokens[index].TryGetTarget(out var token))
                {
                    _tokens.RemoveAt(index);
                    continue;
                }

                token.NotifyChanged();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _paths.EnsureCreated();
        var (global, notice) = await LoadGlobalAsync(cancellationToken).ConfigureAwait(false);
        _global = global;
        LastLoadNotice = notice;
        await SyncProfileLayerLockedAsync(cancellationToken).ConfigureAwait(false);
        _current = BuildSnapshot();
        _initialized = true;

        if (notice is not null)
        {
            _logger.LogWarning("Settings recovery notice: {Message}", notice.Message);
        }
    }

    private async Task<(JsonObject Global, SettingsLoadNotice? Notice)> LoadGlobalAsync(
        CancellationToken cancellationToken)
    {
        var settingsPath = _paths.SettingsFile;
        if (!File.Exists(settingsPath))
        {
            return (new JsonObject(), null);
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return (new JsonObject(), new SettingsLoadNotice(
                $"Settings could not be read: {exception.Message}. Built-in defaults are in use.",
                null,
                RecoveredFromBackup: false));
        }
        catch (UnauthorizedAccessException exception)
        {
            return (new JsonObject(), new SettingsLoadNotice(
                $"Settings could not be read: {exception.Message}. Built-in defaults are in use.",
                null,
                RecoveredFromBackup: false));
        }

        try
        {
            var global = ParseAndMigrate(text);
            EnsureLegacyOnboarding(global);
            ValidateSettingsDocument(global);
            return (global, null);
        }
        catch (Exception exception) when (IsInvalidSettings(exception))
        {
            _logger.LogWarning(exception, "The settings file is invalid and will be quarantined.");
            return await RecoverFromCorruptAsync(exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pre-schema settings defaulted to a completed onboarding flow; every loaded global
    /// document without an explicit onboarding section keeps that behavior.
    /// </summary>
    private static void EnsureLegacyOnboarding(JsonObject document)
    {
        if (document.ContainsKey("onboarding"))
        {
            return;
        }

        document["onboarding"] = new JsonObject { ["completed"] = true };
    }

    /// <summary>
    /// Deserializes the merged document as the typed settings model so wrong-typed
    /// values fail the load and trigger recovery instead of leaking JsonExceptions.
    /// </summary>
    private static void ValidateSettingsDocument(JsonObject global)
    {
        var effective = SettingsDefaults.CreateCopy();
        effective["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        MergeInto(
            effective,
            global,
            string.Empty,
            ConfigSource.Global("validation"),
            new Dictionary<string, ConfigSource>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<ConfigSource>>(StringComparer.OrdinalIgnoreCase));
        _ = DeserializeSettings(effective);
    }

    private static AppSettings DeserializeSettings(JsonObject effective)
    {
        var settings = effective.Deserialize(SettingsJsonContext.Default.AppSettings)
            ?? throw new InvalidOperationException("The effective settings document could not be deserialized.");
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    private static bool IsInvalidSettings(Exception exception) =>
        exception is JsonException
            or InvalidDataException
            or FormatException
            or InvalidOperationException
            or NotSupportedException;

    private async Task<(JsonObject Global, SettingsLoadNotice? Notice)> RecoverFromCorruptAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        string? quarantinedPath = null;
        try
        {
            if (File.Exists(_paths.SettingsFile))
            {
                quarantinedPath = CorruptFileQuarantine.Quarantine(
                    _paths.SettingsFile,
                    _paths.QuarantineDirectory,
                    _timeProvider.GetUtcNow());
            }
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "The invalid settings file could not be quarantined.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "The invalid settings file could not be quarantined.");
        }

        var backupPath = _paths.SettingsBackupFile;
        if (File.Exists(backupPath))
        {
            try
            {
                var backupText = await File.ReadAllTextAsync(backupPath, cancellationToken).ConfigureAwait(false);
                var backup = ParseAndMigrate(backupText);
                EnsureLegacyOnboarding(backup);
                ValidateSettingsDocument(backup);
                await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, backupText, keepBackup: false, cancellationToken)
                    .ConfigureAwait(false);

                var message = "Settings were invalid and have been restored from the last backup.";
                return (backup, new SettingsLoadNotice(message, quarantinedPath, RecoveredFromBackup: true));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or InvalidDataException
                    or FormatException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                _logger.LogWarning(exception, "The settings backup is also invalid.");
                try
                {
                    CorruptFileQuarantine.Quarantine(backupPath, _paths.QuarantineDirectory, _timeProvider.GetUtcNow());
                }
                catch (IOException)
                {
                    // The backup stays in place if it cannot be moved.
                }
                catch (UnauthorizedAccessException)
                {
                    // The backup stays in place if it cannot be moved.
                }
            }
        }

        var defaultsMessage = $"Settings were invalid ({reason}). Built-in defaults are in use.";
        return (new JsonObject(), new SettingsLoadNotice(defaultsMessage, quarantinedPath, RecoveredFromBackup: false));
    }

    private async Task<JsonObject?> LoadProfileLockedAsync(string? profileId, CancellationToken cancellationToken)
    {
        _profile = new JsonObject();
        _loadedProfileId = null;
        if (profileId is null)
        {
            return null;
        }

        try
        {
            ValidateIdentifier(profileId);
        }
        catch (ArgumentException exception)
        {
            _logger.LogWarning(exception, "The profile identifier {ProfileId} is invalid and the layer is skipped.", profileId);
            return null;
        }

        _profile = await LoadLayerDocumentAsync(
            ProfileFile(profileId),
            layerName: $"profile '{profileId}'",
            quarantineOnFailure: true,
            cancellationToken).ConfigureAwait(false) ?? new JsonObject();
        _loadedProfileId = profileId;
        return _profile;
    }

    /// <summary>
    /// Loads the profile document when the active profile id changed. The session layer
    /// wins over the global selection because it is the highest layer, so a
    /// session-scope <c>profile.activeId</c> activates its profile immediately.
    /// </summary>
    private async Task SyncProfileLayerLockedAsync(CancellationToken cancellationToken)
    {
        var activeProfileId = ResolveActiveProfileId();
        if (string.Equals(activeProfileId, _loadedProfileId, StringComparison.Ordinal))
        {
            return;
        }

        await LoadProfileLockedAsync(activeProfileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> LoadLayerDocumentAsync(
        string file,
        string layerName,
        bool quarantineOnFailure,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0 || !File.Exists(file))
        {
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "The {Layer} configuration file {File} could not be read and is ignored.",
                layerName,
                file);
            return null;
        }

        try
        {
            return ParseAndMigrate(text);
        }
        catch (Exception exception) when (IsInvalidSettings(exception))
        {
            // Repository files are never moved aside because git owns them; the layer is
            // skipped and the violation is reported through the log instead.
            _logger.LogWarning(
                exception,
                "The {Layer} configuration file {File} is invalid and is ignored.",
                layerName,
                file);
            if (quarantineOnFailure && File.Exists(file))
            {
                try
                {
                    CorruptFileQuarantine.Quarantine(file, _paths.QuarantineDirectory, _timeProvider.GetUtcNow());
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(cleanupException, "The invalid file could not be quarantined.");
                }
            }

            return null;
        }
    }

    private JsonObject ParseAndMigrate(string text)
    {
        var node = JsonNode.Parse(
            text,
            documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        if (node is not JsonObject document)
        {
            throw new InvalidDataException("The configuration document must be a JSON object.");
        }

        var version = ReadSchemaVersion(document);
        if (version > AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"The configuration schemaVersion {version} is newer than the supported version {AppSettings.CurrentSchemaVersion}.");
        }

        _migrator.Migrate(document, version);
        return document;
    }

    private static int ReadSchemaVersion(JsonObject document)
    {
        if (!ConfigSnapshot.TryGetProperty(document, "schemaVersion", out var versionNode) || versionNode is null)
        {
            return 0;
        }

        if (versionNode is not JsonValue versionValue ||
            !versionValue.TryGetValue<int>(out var version) ||
            version < 1)
        {
            throw new InvalidDataException("The configuration schemaVersion is invalid.");
        }

        return version;
    }

    private ConfigSnapshot BuildSnapshot()
    {
        var effective = SettingsDefaults.CreateCopy();
        effective["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        var sources = new Dictionary<string, ConfigSource>(StringComparer.OrdinalIgnoreCase);
        var contributions = new Dictionary<string, List<ConfigSource>>(StringComparer.OrdinalIgnoreCase);
        RecordLeaves(effective, string.Empty, ConfigSource.Defaults, sources);

        foreach (var (document, source) in Layers())
        {
            MergeInto(effective, document, string.Empty, source, sources, contributions);
        }

        effective["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        var settings = DeserializeSettings(effective);

        var contributionMap = new Dictionary<string, IReadOnlyList<ConfigSource>>(
            contributions.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in contributions)
        {
            contributionMap[pair.Key] = pair.Value;
        }

        return new ConfigSnapshot(settings, effective, sources, contributionMap);
    }

    private IEnumerable<(JsonObject Document, ConfigSource Source)> Layers()
    {
        yield return (_global, ConfigSource.Global(_paths.SettingsFile));
        if (ResolveActiveProfileId() is { } profileId)
        {
            yield return (_profile, new ConfigSource(ConfigScope.Profile, $"profiles/{profileId}.json"));
        }

        if (_teamRepositoryRoot is not null)
        {
            yield return (_teamProject, new ConfigSource(ConfigScope.TeamProject, TeamProjectFile));
        }

        if (_localProjectId is not null)
        {
            yield return (_localProject, new ConfigSource(ConfigScope.LocalProject, LocalProjectFile));
        }

        yield return (_runConfiguration, new ConfigSource(ConfigScope.RunConfiguration, "run-configuration"));
        yield return (_deviceAll, new ConfigSource(ConfigScope.Device, DeviceOverrideTarget.AllDevices.Origin));
        foreach (var group in OrderedKeys(_deviceGroups))
        {
            yield return (_deviceGroups[group], new ConfigSource(ConfigScope.Device, DeviceOverrideTarget.Group(group).Origin));
        }

        foreach (var serial in OrderedKeys(_deviceSerials))
        {
            yield return (_deviceSerials[serial], new ConfigSource(ConfigScope.Device, DeviceOverrideTarget.Serial(serial).Origin));
        }

        yield return (_session, new ConfigSource(ConfigScope.Session, "session"));
    }

    private static IEnumerable<string> OrderedKeys(Dictionary<string, JsonObject> documents) =>
        documents.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The session layer wins over the global selection because it is the highest layer,
    /// so a session-scope <c>profile.activeId</c> activates its profile immediately.
    /// </summary>
    private string? ResolveActiveProfileId()
    {
        if (TryGetDocumentString(_session, SettingsPaths.ActiveProfileId, out var sessionProfile) &&
            !string.IsNullOrWhiteSpace(sessionProfile))
        {
            return sessionProfile;
        }

        return TryGetDocumentString(_global, SettingsPaths.ActiveProfileId, out var globalProfile) &&
               !string.IsNullOrWhiteSpace(globalProfile)
            ? globalProfile
            : null;
    }

    private static bool TryGetDocumentString(JsonObject document, string path, out string? value)
    {
        value = null;
        JsonNode? current = document;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !ConfigSnapshot.TryGetProperty(obj, segment, out var next))
            {
                return false;
            }

            current = next;
        }

        if (current is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static void MergeInto(
        JsonObject target,
        JsonObject overrides,
        string prefix,
        ConfigSource source,
        Dictionary<string, ConfigSource> sources,
        Dictionary<string, List<ConfigSource>> contributions)
    {
        foreach (var property in overrides)
        {
            var path = Combine(prefix, property.Key);
            var actualKey = FindKey(target, property.Key) ?? property.Key;
            var existing = ConfigSnapshot.TryGetProperty(target, property.Key, out var existingValue) ? existingValue : null;

            if (existing is JsonObject existingObject && property.Value is JsonObject overrideObject)
            {
                MergeInto(existingObject, overrideObject, path, source, sources, contributions);
                continue;
            }

            target[actualKey] = property.Value?.DeepClone();
            RemoveSourcesUnder(path, sources);
            RemoveContributionsBelow(path, contributions);
            RecordLeaves(property.Value, path, source, sources, contributions);
        }
    }

    private static void RecordLeaves(
        JsonNode? node,
        string path,
        ConfigSource source,
        Dictionary<string, ConfigSource> sources,
        Dictionary<string, List<ConfigSource>>? contributions = null)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var property in obj)
            {
                RecordLeaves(property.Value, Combine(path, property.Key), source, sources, contributions);
            }

            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        sources[path] = source;
        if (contributions is null)
        {
            return;
        }

        if (!contributions.TryGetValue(path, out var list))
        {
            contributions[path] = list = [];
        }

        list.Add(source);
    }

    private static void RemoveSourcesUnder(string path, Dictionary<string, ConfigSource> sources)
    {
        var prefix = path + ".";
        var keys = sources.Keys
            .Where(key => string.Equals(key, path, StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
        {
            sources.Remove(key);
        }
    }

    /// <summary>
    /// Drops contribution history for descendants of a replaced node. The entry for the
    /// node itself is kept so every layer that set it stays visible in the source map.
    /// </summary>
    private static void RemoveContributionsBelow(string path, Dictionary<string, List<ConfigSource>> contributions)
    {
        var prefix = path + ".";
        var keys = contributions.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
        {
            contributions.Remove(key);
        }
    }

    private static string Combine(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;

    private static string? FindKey(JsonObject obj, string name)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }

    private static void SetNode(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var key = FindKey(current, segments[index]) ?? segments[index];
            if (current[key] is not JsonObject child)
            {
                child = new JsonObject();
                current[key] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
    }

    private static bool RemoveNode(JsonObject root, string path)
    {
        var segments = path.Split('.');
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var key = FindKey(current, segments[index]);
            if (key is null || current[key] is not JsonObject child)
            {
                return false;
            }

            current = child;
        }

        var leaf = FindKey(current, segments[^1]);
        if (leaf is null)
        {
            return false;
        }

        return current.Remove(leaf);
    }

    private static void AddSchemaVersion(JsonObject document)
    {
        document["schemaVersion"] = AppSettings.CurrentSchemaVersion;
    }

    private static (JsonObject Document, SettingsValidationResult Validation, IReadOnlyList<string> Excluded) PrepareImport(
        string json,
        ConfigScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var excluded = new List<string>();
        JsonObject parsed;
        try
        {
            parsed = ParseDocument(json);
        }
        catch (JsonException exception)
        {
            return (
                new JsonObject(),
                new SettingsValidationResult(
                [
                    new SettingsValidationIssue(
                        SettingsTransfer.NormalizeJsonPath(exception.Path),
                        $"Invalid JSON: {exception.Message}",
                        SettingsIssueSeverity.Error),
                ]),
                excluded);
        }
        catch (InvalidDataException exception)
        {
            return (
                new JsonObject(),
                new SettingsValidationResult(
                    [new SettingsValidationIssue(string.Empty, exception.Message, SettingsIssueSeverity.Error)]),
                excluded);
        }

        var unwrapped = SettingsTransfer.UnwrapBundle(parsed, scope, out var bundleError);
        if (unwrapped is null)
        {
            return (
                new JsonObject(),
                new SettingsValidationResult(
                [
                    new SettingsValidationIssue(
                        "scopes",
                        bundleError ?? "The export bundle could not be read.",
                        SettingsIssueSeverity.Error),
                ]),
                excluded);
        }

        var document = SettingsTransfer.Sanitize(unwrapped, excluded);
        var issues = new List<SettingsValidationIssue>();
        ValidateDocument(document, issues);
        return (document, new SettingsValidationResult(issues), excluded);
    }

    private static JsonObject ParseDocument(string text) =>
        JsonNode.Parse(text, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
            ?? throw new InvalidDataException("The settings document must be a JSON object.");

    private static void ValidateDocument(JsonObject document, List<SettingsValidationIssue> issues)
    {
        SettingsTransfer.CollectCaseConflicts(document, string.Empty, issues);
        if (ConfigSnapshot.TryGetProperty(document, "schemaVersion", out var versionNode) && versionNode is not null)
        {
            if (versionNode is not JsonValue versionValue ||
                !versionValue.TryGetValue<int>(out var version) ||
                version < 1)
            {
                issues.Add(new SettingsValidationIssue(
                    "schemaVersion",
                    "schemaVersion must be a positive integer.",
                    SettingsIssueSeverity.Error));
            }
            else if (version > AppSettings.CurrentSchemaVersion)
            {
                issues.Add(new SettingsValidationIssue(
                    "schemaVersion",
                    $"schemaVersion {version} is newer than the supported version {AppSettings.CurrentSchemaVersion}.",
                    SettingsIssueSeverity.Error));
            }
        }

        var effective = SettingsDefaults.CreateCopy();
        SettingsTransfer.Merge(effective, document);
        effective["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        try
        {
            _ = DeserializeSettings(effective);
        }
        catch (JsonException exception)
        {
            var path = SettingsTransfer.NormalizeJsonPath(exception.Path);
            issues.Add(new SettingsValidationIssue(
                path,
                $"The value at \"{(path.Length == 0 ? "document" : path)}\" is invalid: {exception.Message}",
                SettingsIssueSeverity.Error));
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new SettingsValidationIssue(string.Empty, exception.Message, SettingsIssueSeverity.Error));
        }

        SettingsTransfer.CollectUnknownKeys(document, issues);
    }

    private JsonObject? ScopeDocumentLocked(ConfigScope scope) => scope switch
    {
        ConfigScope.Global => _global,
        ConfigScope.Profile => _loadedProfileId is null ? null : _profile,
        ConfigScope.TeamProject => _teamRepositoryRoot is null ? null : _teamProject,
        ConfigScope.LocalProject => _localProjectId is null ? null : _localProject,
        ConfigScope.RunConfiguration => _runConfiguration,
        ConfigScope.Session => _session,
        ConfigScope.Device => _deviceAll,
        _ => null,
    };

    private void RequireWritableLayerLocked(ConfigScope scope)
    {
        switch (scope)
        {
            case ConfigScope.TeamProject:
                RequireTeamProjectLocked();
                break;
            case ConfigScope.LocalProject:
                RequireLocalProjectLocked();
                break;
            case ConfigScope.Profile when _loadedProfileId is null:
                throw new InvalidOperationException("Select a profile before changing the profile layer.");
        }
    }

    private async Task<string?> SaveLayerLockedAsync(
        ConfigScope scope,
        JsonObject document,
        CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case ConfigScope.Global:
                await SaveGlobalLockedAsync(document, cancellationToken).ConfigureAwait(false);
                return ExistingFile(_paths.SettingsBackupFile);
            case ConfigScope.TeamProject:
                RequireTeamProjectLocked();
                await SaveTeamProjectLockedAsync(document, cancellationToken).ConfigureAwait(false);
                return null;
            case ConfigScope.LocalProject:
                RequireLocalProjectLocked();
                await SaveLocalProjectLockedAsync(document, cancellationToken).ConfigureAwait(false);
                return ExistingFile(LocalProjectFile + ".bak");
            case ConfigScope.Profile:
                var profileId = _loadedProfileId
                    ?? throw new InvalidOperationException("Select a profile before changing the profile layer.");
                var file = ProfileFile(profileId);
                var json = document.ToJsonString(DroidDeckJson.Options);
                await AtomicFile.WriteAllTextAsync(file, json, keepBackup: true, cancellationToken)
                    .ConfigureAwait(false);
                _profile = document;
                return ExistingFile(file + ".bak");
            case ConfigScope.RunConfiguration:
                _runConfiguration = document;
                return null;
            case ConfigScope.Session:
                _session = document;
                return null;
            default:
                throw new NotSupportedException("This layer cannot be written directly.");
        }
    }

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;

    private static void ValidateImportScope(ConfigScope scope)
    {
        if (scope is ConfigScope.BuiltInDefaults or ConfigScope.Device)
        {
            throw new NotSupportedException(
                scope == ConfigScope.BuiltInDefaults
                    ? "Built-in defaults cannot be imported."
                    : "Use SetDeviceOverrideAsync for device and device group overrides.");
        }
    }

    private static JsonNode? GetNode(JsonObject document, string path)
    {
        JsonNode? current = document;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !ConfigSnapshot.TryGetProperty(obj, segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private async Task SaveGlobalLockedAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var json = document.ToJsonString(DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, json, keepBackup: true, cancellationToken)
            .ConfigureAwait(false);
        _global = document;
    }

    private async Task SaveTeamProjectLockedAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var violations = TeamProjectConfigValidator.Validate(document);
        if (violations.Violations.Count > 0)
        {
            throw new TeamConfigViolationException(violations.Violations);
        }

        // No backup file: a stray project.json.bak would dirty the repository.
        var json = document.ToJsonString(DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(TeamProjectFile, json, keepBackup: false, cancellationToken)
            .ConfigureAwait(false);
        _teamProject = document;
    }

    private async Task SaveLocalProjectLockedAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var json = document.ToJsonString(DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(LocalProjectFile, json, keepBackup: true, cancellationToken)
            .ConfigureAwait(false);
        _localProject = document;
    }

    private string TeamProjectFile => Path.Combine(_teamRepositoryRoot ?? string.Empty, ".droiddeck", "project.json");

    private string LocalProjectFile =>
        Path.Combine(_paths.ProjectsDirectory, "overrides", (_localProjectId ?? string.Empty) + ".json");

    private string ProfileFile(string profileId) => Path.Combine(_paths.DataDirectory, "profiles", profileId + ".json");

    private void RequireTeamProjectLocked()
    {
        if (_teamRepositoryRoot is null)
        {
            throw new InvalidOperationException(
                "Attach a project with SetProjectContextAsync before writing team-shared configuration.");
        }
    }

    private void RequireLocalProjectLocked()
    {
        if (_localProjectId is null)
        {
            throw new InvalidOperationException(
                "Attach a project with SetProjectContextAsync before writing local project overrides.");
        }
    }

    private JsonObject DeviceDocumentLocked(DeviceOverrideTarget target) => target.Kind switch
    {
        DeviceOverrideKind.All => _deviceAll,
        DeviceOverrideKind.Group => GetOrAddDeviceDocument(_deviceGroups, target.Key),
        DeviceOverrideKind.Serial => GetOrAddDeviceDocument(_deviceSerials, target.Key),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target.Kind, null),
    };

    private static JsonObject GetOrAddDeviceDocument(Dictionary<string, JsonObject> documents, string key)
    {
        if (!documents.TryGetValue(key, out var document))
        {
            documents[key] = document = new JsonObject();
        }

        return document;
    }

    private void StoreDeviceDocumentLocked(DeviceOverrideTarget target, JsonObject document)
    {
        switch (target.Kind)
        {
            case DeviceOverrideKind.All:
                _deviceAll = document;
                break;
            case DeviceOverrideKind.Group:
                _deviceGroups[target.Key] = document;
                break;
            case DeviceOverrideKind.Serial:
                _deviceSerials[target.Key] = document;
                break;
        }
    }

    private static void ValidateWritableScope(ConfigScope scope)
    {
        if (scope is ConfigScope.BuiltInDefaults or ConfigScope.Profile or ConfigScope.Device)
        {
            throw new NotSupportedException(scope switch
            {
                ConfigScope.BuiltInDefaults => "Built-in defaults cannot be overridden.",
                ConfigScope.Profile => "Profile documents are managed by the profile service.",
                _ => "Use SetDeviceOverrideAsync for device and device group overrides.",
            });
        }
    }

    private static void ValidateTarget(DeviceOverrideTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is DeviceOverrideKind.Group or DeviceOverrideKind.Serial &&
            string.IsNullOrWhiteSpace(target.Key))
        {
            throw new ArgumentException("A device group name or device serial is required.", nameof(target));
        }
    }

    private static void ValidateIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Length > 128 ||
            identifier.Contains("..", StringComparison.Ordinal) ||
            !identifier.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "Identifiers must be 1-128 characters of letters, digits, dots, underscores, or hyphens.",
                nameof(identifier));
        }
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Split('.').Any(static segment => segment.Length == 0))
        {
            throw new ArgumentException("Configuration paths must use non-empty dotted segments.", nameof(path));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
