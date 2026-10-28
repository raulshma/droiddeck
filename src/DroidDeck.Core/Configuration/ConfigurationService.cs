using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// Loads the embedded defaults and the global settings file, merges them, and
/// tracks which layer supplied each effective value.
/// </summary>
public sealed class ConfigurationService : IConfigurationService, IDisposable
{
    private readonly AppPaths _paths;
    private readonly ILogger<ConfigurationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile ConfigSnapshot _current;
    private JsonObject _global = new();
    private bool _initialized;
    private bool _disposed;

    /// <summary>Creates a configuration service for the provided data paths.</summary>
    public ConfigurationService(
        AppPaths paths,
        ILogger<ConfigurationService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<ConfigurationService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = BuildSnapshot(new JsonObject());
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
        ValidateScope(scope);
        ValidatePath(path);
        var node = JsonSerializer.SerializeToNode(value, DroidDeckJson.Options);

        ConfigChangedEventArgs? change = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            var candidate = (JsonObject)_global.DeepClone();
            SetNode(candidate, path, node?.DeepClone());
            AddSchemaVersion(candidate);
            await SaveAsync(candidate, cancellationToken).ConfigureAwait(false);

            _global = candidate;
            _current = BuildSnapshot(_global);
            var source = _current.GetSource(path);
            change = new ConfigChangedEventArgs(path, scope, source, ConfigChangeKind.Set);
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
        ValidateScope(scope);
        ValidatePath(path);

        ConfigChangedEventArgs? change = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            var candidate = (JsonObject)_global.DeepClone();
            if (RemoveNode(candidate, path))
            {
                AddSchemaVersion(candidate);
                await SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                _global = candidate;
                _current = BuildSnapshot(_global);
            }

            var source = _current.GetSource(path);
            change = new ConfigChangedEventArgs(path, scope, source, ConfigChangeKind.Reset);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, change);
    }

    /// <inheritdoc />
    public ConfigSource GetSource(string path) => _current.GetSource(path);

    /// <inheritdoc />
    public T? GetValue<T>(string path)
    {
        var node = _current.GetValue(path);
        if (node is null)
        {
            return default;
        }

        try
        {
            return node.Deserialize<T>(DroidDeckJson.Options);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The setting {Path} could not be read as {Type}.", path, typeof(T).Name);
            return default;
        }
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
        var result = await LoadGlobalAsync(cancellationToken).ConfigureAwait(false);
        _global = result.Global;
        _current = result.Snapshot;
        LastLoadNotice = result.Notice;
        _initialized = true;

        if (result.Notice is not null)
        {
            _logger.LogWarning("Settings recovery notice: {Message}", result.Notice.Message);
        }
    }

    private async Task<LoadedSettings> LoadGlobalAsync(CancellationToken cancellationToken)
    {
        var settingsPath = _paths.SettingsFile;
        if (!File.Exists(settingsPath))
        {
            return LoadedSettings.Defaults(BuildSnapshot(new JsonObject()));
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            return LoadedSettings.Defaults(
                BuildSnapshot(new JsonObject()),
                new SettingsLoadNotice(
                    $"Settings could not be read: {exception.Message}. Built-in defaults are in use.",
                    null,
                    RecoveredFromBackup: false));
        }
        catch (UnauthorizedAccessException exception)
        {
            return LoadedSettings.Defaults(
                BuildSnapshot(new JsonObject()),
                new SettingsLoadNotice(
                    $"Settings could not be read: {exception.Message}. Built-in defaults are in use.",
                    null,
                    RecoveredFromBackup: false));
        }

        try
        {
            var global = ParseGlobal(text);
            MigrateLegacyOnboarding(global);
            return new LoadedSettings(global, BuildSnapshot(global), Notice: null);
        }
        catch (Exception exception) when (IsInvalidSettings(exception))
        {
            _logger.LogWarning(exception, "The settings file is invalid and will be quarantined.");
            return await RecoverFromCorruptAsync(exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsInvalidSettings(Exception exception) =>
        exception is JsonException
            or InvalidDataException
            or FormatException
            or InvalidOperationException
            or NotSupportedException;

    private async Task<LoadedSettings> RecoverFromCorruptAsync(
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
                var backup = ParseGlobal(backupText);
                MigrateLegacyOnboarding(backup);
                var backupSnapshot = BuildSnapshot(backup);
                await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, backupText, keepBackup: false, cancellationToken)
                    .ConfigureAwait(false);

                var message = "Settings were invalid and have been restored from the last backup.";
                return new LoadedSettings(
                    backup,
                    backupSnapshot,
                    new SettingsLoadNotice(message, quarantinedPath, RecoveredFromBackup: true));
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
        return LoadedSettings.Defaults(
            BuildSnapshot(new JsonObject()),
            new SettingsLoadNotice(defaultsMessage, quarantinedPath, RecoveredFromBackup: false));
    }

    private static JsonObject ParseGlobal(string text)
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
            throw new InvalidDataException("The settings document must be a JSON object.");
        }

        var schemaVersion = AppSettings.CurrentSchemaVersion;
        if (ConfigSnapshot.TryGetProperty(document, "schemaVersion", out var versionNode) && versionNode is not null)
        {
            if (versionNode is not JsonValue versionValue ||
                !versionValue.TryGetValue<int>(out schemaVersion) ||
                schemaVersion < 1)
            {
                throw new InvalidDataException("The settings schemaVersion is invalid.");
            }

            if (schemaVersion > AppSettings.CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"The settings schemaVersion {schemaVersion} is newer than the supported version {AppSettings.CurrentSchemaVersion}.");
            }
        }

        document["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        return document;
    }

    private static void MigrateLegacyOnboarding(JsonObject document)
    {
        if (document.ContainsKey("onboarding"))
        {
            return;
        }

        document["onboarding"] = new JsonObject
        {
            ["completed"] = true,
        };
    }

    private ConfigSnapshot BuildSnapshot(JsonObject global)
    {
        var effective = SettingsDefaults.CreateCopy();
        AddSchemaVersion(effective);
        var sources = new Dictionary<string, ConfigSource>(StringComparer.OrdinalIgnoreCase);
        RecordLeaves(effective, string.Empty, ConfigSource.Defaults, sources);

        var globalSource = ConfigSource.Global(_paths.SettingsFile);
        MergeInto(effective, global, string.Empty, globalSource, sources);
        effective["schemaVersion"] = AppSettings.CurrentSchemaVersion;

        var settings = effective.Deserialize(SettingsJsonContext.Default.AppSettings)
            ?? throw new InvalidOperationException("The effective settings document could not be deserialized.");
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;

        return new ConfigSnapshot(settings, effective, sources);
    }

    private static void MergeInto(
        JsonObject target,
        JsonObject overrides,
        string prefix,
        ConfigSource source,
        Dictionary<string, ConfigSource> sources)
    {
        foreach (var property in overrides)
        {
            var path = Combine(prefix, property.Key);
            var actualKey = FindKey(target, property.Key) ?? property.Key;
            var existing = ConfigSnapshot.TryGetProperty(target, property.Key, out var existingValue) ? existingValue : null;

            if (existing is JsonObject existingObject && property.Value is JsonObject overrideObject)
            {
                MergeInto(existingObject, overrideObject, path, source, sources);
                continue;
            }

            target[actualKey] = property.Value?.DeepClone();
            RemoveSourcesUnder(path, sources);
            RecordLeaves(property.Value, path, source, sources);
        }
    }

    private static void RecordLeaves(
        JsonNode? node,
        string path,
        ConfigSource source,
        Dictionary<string, ConfigSource> sources)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var property in obj)
            {
                RecordLeaves(property.Value, Combine(path, property.Key), source, sources);
            }

            return;
        }

        if (!string.IsNullOrEmpty(path))
        {
            sources[path] = source;
        }
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

    private async Task SaveAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var json = document.ToJsonString(DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(_paths.SettingsFile, json, keepBackup: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void ValidateScope(ConfigScope scope)
    {
        if (scope != ConfigScope.Global)
        {
            throw new NotSupportedException(
                $"The {scope} scope is not implemented yet. Only the Global scope is available in this build.");
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

    private readonly record struct LoadedSettings(
        JsonObject Global,
        ConfigSnapshot Snapshot,
        SettingsLoadNotice? Notice)
    {
        public static LoadedSettings Defaults(ConfigSnapshot snapshot, SettingsLoadNotice? notice = null) =>
            new(new JsonObject(), snapshot, notice);
    }
}
