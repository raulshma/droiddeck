using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>The outcome of a schema-validated settings write.</summary>
/// <param name="Errors">The field errors that blocked the write; empty when it was saved.</param>
public sealed record PluginSettingsWriteResult(IReadOnlyList<PluginSettingsFieldError> Errors)
{
    /// <summary>Gets a saved result.</summary>
    public static PluginSettingsWriteResult Ok { get; } = new([]);

    /// <summary>Gets a value indicating whether the value was saved.</summary>
    public bool IsSaved => Errors.Count == 0;
}

/// <summary>Thrown when a raw settings write fails schema validation.</summary>
public sealed class PluginSettingsValidationException : Exception
{
    /// <summary>Creates the exception for a set of field errors.</summary>
    public PluginSettingsValidationException(IReadOnlyList<PluginSettingsFieldError> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Gets the field errors that blocked the write.</summary>
    public IReadOnlyList<PluginSettingsFieldError> Errors { get; }

    private static string BuildMessage(IReadOnlyList<PluginSettingsFieldError> errors)
        => errors.Count == 0
            ? "The settings value is invalid."
            : errors[0].ToString();
}

/// <summary>A sanitized copy of a plugin's settings for export.</summary>
/// <param name="SettingsVersion">The schema version the values were written under.</param>
/// <param name="Values">The values with every secret field omitted.</param>
/// <param name="ExcludedSecrets">The secret field names that were omitted.</param>
public sealed record PluginSettingsExport(
    int SettingsVersion,
    JsonObject Values,
    IReadOnlyList<string> ExcludedSecrets)
{
    /// <summary>Serializes the sanitized export.</summary>
    public string ToJson() => new JsonObject
    {
        ["settingsVersion"] = SettingsVersion,
        ["values"] = Values.DeepClone(),
    }.ToJsonString(DroidDeckJson.Options);
}

/// <summary>
/// A schema-driven plugin settings store persisted as JSON beside the plugin
/// assembly, with secret fields stored in the host's DPAPI-backed secret store.
/// The settings document keeps three kinds of entries:
/// <c>settingsVersion</c> (the schema version the values were written under),
/// <c>values</c> (all values, including unknown keys, which are preserved), and
/// <c>secrets</c> (the names of secret fields that are set). Secret values never
/// appear in the file: they live only in the secret store under the
/// <c>&lt;pluginId&gt;.&lt;fieldKey&gt;</c> name.
/// </summary>
public sealed class SchemaPluginSettings : IPluginSettings, IDisposable
{
    private readonly string _filePath;
    private readonly PluginSettingsSchema _schema;
    private readonly string _pluginId;
    private readonly ISecretStore _secrets;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject? _values;
    private HashSet<string>? _secretMarkers;
    private int _storedVersion;
    private bool _disposed;

    /// <summary>Creates a schema-driven settings store rooted at a JSON file.</summary>
    public SchemaPluginSettings(
        string filePath,
        PluginSettingsSchema schema,
        string pluginId,
        ISecretStore secretStore,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(secretStore);
        _filePath = Path.GetFullPath(filePath);
        _schema = schema;
        _pluginId = pluginId;
        _secrets = secretStore;
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the settings file path.</summary>
    public string FilePath => _filePath;

    /// <summary>Gets the schema the store validates against.</summary>
    public PluginSettingsSchema Schema => _schema;

    /// <summary>Gets the DPAPI secret-store name of a secret field.</summary>
    public string SecretName(string fieldKey) => BuildSecretName(_pluginId, fieldKey);

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var field = _schema.Find(key);
            if (field is { Type: PluginSettingsFieldType.Secret })
            {
                return await _secrets.GetAsync(SecretName(key), cancellationToken).ConfigureAwait(false);
            }

            var node = state.Values[key] ?? field?.Default?.DeepClone();
            return node switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                var node2 => node2.ToJsonString(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = await SetTextAsync(key, value, cancellationToken).ConfigureAwait(false);
        if (!result.IsSaved)
        {
            throw new PluginSettingsValidationException(result.Errors);
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (values, markers) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var changed = values.Remove(key) | markers.Remove(key);
            var field = _schema.Find(key);
            if (field is { Type: PluginSettingsFieldType.Secret })
            {
                await _secrets.RemoveAsync(SecretName(key), cancellationToken).ConfigureAwait(false);
                changed = true;
            }

            if (changed)
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads the effective typed value of a setting: the stored value, or the
    /// schema default when absent. Secret fields return <see langword="null"/>;
    /// plugins read the decrypted value through <see cref="GetAsync"/> and the UI
    /// uses <see cref="IsSecretSetAsync"/> so plaintext never reaches a control.
    /// </summary>
    public async Task<JsonNode?> GetNodeAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (values, _) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var field = _schema.Find(key);
            if (field is { Type: PluginSettingsFieldType.Secret })
            {
                return null;
            }

            return values[key]?.DeepClone() ?? field?.Default?.DeepClone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates and writes a typed value. Invalid values are not saved; the
    /// returned errors are keyed by field for inline display.
    /// </summary>
    public async Task<PluginSettingsWriteResult> SetNodeAsync(
        string key,
        JsonNode? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = _schema.Find(key);
            if (field is { Type: PluginSettingsFieldType.Secret })
            {
                return new PluginSettingsWriteResult(
                [
                    new PluginSettingsFieldError(
                        key,
                        "settings.value.secretChannel",
                        "Secret values are set through the Set secret action, not as a plain value."),
                ]);
            }

            var errors = field is null
                ? new List<PluginSettingsFieldError>()
                : PluginSettingsValidator.Validate(field, value).ToList();
            if (errors.Count > 0)
            {
                return new PluginSettingsWriteResult(errors);
            }

            var (values, _) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value.DeepClone();
            }

            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return PluginSettingsWriteResult.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates and stores a secret field in the DPAPI secret store. The
    /// settings file only records that the field is set.
    /// </summary>
    public async Task<PluginSettingsWriteResult> SetSecretAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = _schema.Find(key);
            if (field is null || field.Type != PluginSettingsFieldType.Secret)
            {
                return new PluginSettingsWriteResult(
                [
                    new PluginSettingsFieldError(
                        key,
                        "settings.value.secretField",
                        "Only fields declared with the secret format can hold a secret."),
                ]);
            }

            await _secrets.SetAsync(SecretName(key), value, cancellationToken).ConfigureAwait(false);
            var (_, markers) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            markers.Add(key);
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            return PluginSettingsWriteResult.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a secret field from the secret store and its presence marker.</summary>
    public async Task ClearSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = _schema.Find(key);
            if (field is not { Type: PluginSettingsFieldType.Secret })
            {
                return;
            }

            await _secrets.RemoveAsync(SecretName(key), cancellationToken).ConfigureAwait(false);
            var (_, markers) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (markers.Remove(key))
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets whether a secret field has a stored value. Never returns the value.</summary>
    public async Task<bool> IsSecretSetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, markers) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return markers.Contains(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates every declared field and returns all field errors, keyed by
    /// field. Required fields without a stored value or default are reported.
    /// </summary>
    public async Task<IReadOnlyList<PluginSettingsFieldError>> ValidateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (values, _) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return ValidateCore(values);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Returns a sanitized copy of the settings for export and diagnostics:
    /// secret fields are omitted entirely and their names are reported, while
    /// unknown keys are preserved so exports round-trip.
    /// </summary>
    public async Task<PluginSettingsExport> ExportAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (values, _) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var copy = (JsonObject)values.DeepClone();
            var excluded = new List<string>();
            foreach (var field in _schema.Fields)
            {
                if (field.Type != PluginSettingsFieldType.Secret)
                {
                    continue;
                }

                // Secret values live in the secret store, so this only removes a
                // legacy plaintext leftover, but exports must never carry one.
                if (copy.Remove(field.Key))
                {
                    _logger.LogWarning(
                        "A plaintext value for secret field '{Field}' was found in settings and omitted from the export.",
                        field.Key);
                }

                excluded.Add(field.Key);
            }

            return new PluginSettingsExport(_storedVersion, copy, excluded);
        }
        finally
        {
            _gate.Release();
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

    private List<PluginSettingsFieldError> ValidateCore(JsonObject values)
    {
        var errors = new List<PluginSettingsFieldError>();
        foreach (var field in _schema.Fields)
        {
            var value = values[field.Key] ?? field.Default?.DeepClone();
            if (value is null)
            {
                if (field.Required)
                {
                    errors.Add(new PluginSettingsFieldError(
                        field.Key,
                        "settings.value.required",
                        $"'{field.DisplayName}' is required."));
                }

                continue;
            }

            errors.AddRange(PluginSettingsValidator.Validate(field, value));
        }

        return errors;
    }

    private async Task<PluginSettingsWriteResult> SetTextAsync(
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var field = _schema.Find(key);
            if (field is { Type: PluginSettingsFieldType.Secret })
            {
                PluginSettingsWriteResult result;
                if (value is null)
                {
                    await _secrets.RemoveAsync(SecretName(key), cancellationToken).ConfigureAwait(false);
                    var (_, markers) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (markers.Remove(key))
                    {
                        await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
                    }

                    result = PluginSettingsWriteResult.Ok;
                }
                else
                {
                    await _secrets.SetAsync(SecretName(key), value, cancellationToken).ConfigureAwait(false);
                    var (_, markers) = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                    markers.Add(key);
                    await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
                    result = PluginSettingsWriteResult.Ok;
                }

                return result;
            }
        }
        finally
        {
            _gate.Release();
        }

        return await SetNodeAsync(key, ParseText(key, value), cancellationToken).ConfigureAwait(false);
    }

    private JsonNode? ParseText(string key, string? value)
    {
        if (value is null)
        {
            return null;
        }

        var field = _schema.Find(key);
        if (field is null)
        {
            // Unknown keys are preserved verbatim as strings.
            return JsonValue.Create(value);
        }

        return field.Type switch
        {
            PluginSettingsFieldType.Number => double.TryParse(
                value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? JsonValue.Create(number)
                : JsonValue.Create(value),
            PluginSettingsFieldType.WholeNumber => long.TryParse(
                value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? JsonValue.Create(integer)
                : JsonValue.Create(value),
            PluginSettingsFieldType.Boolean => bool.TryParse(value, out var flag)
                ? JsonValue.Create(flag)
                : JsonValue.Create(value),
            PluginSettingsFieldType.List or PluginSettingsFieldType.Group =>
                TryParseJson(value) ?? JsonValue.Create(value),
            _ => JsonValue.Create(value),
        };
    }

    private static JsonNode? TryParseJson(string value)
    {
        try
        {
            return JsonNode.Parse(value, documentOptions: DroidDeckJson.DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(JsonObject Values, HashSet<string> SecretMarkers)> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (_values is not null)
        {
            return (_values, _secretMarkers!);
        }

        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);

        // Settings written before a plugin shipped a schema are flat key/value
        // objects; their keys move into values so nothing is lost.
        JsonObject values;
        if (document.TryGetPropertyValue("values", out var valuesNode) && valuesNode is JsonObject valuesObject)
        {
            values = valuesObject;
        }
        else
        {
            values = new JsonObject();
            foreach (var (key, value) in document)
            {
                if (key is not ("settingsVersion" or "values" or "secrets"))
                {
                    values[key] = value?.DeepClone();
                }
            }
        }

        var markers = new HashSet<string>(StringComparer.Ordinal);
        if (document.TryGetPropertyValue("secrets", out var secretsNode) && secretsNode is JsonArray secretsArray)
        {
            foreach (var name in secretsArray)
            {
                if (name is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    markers.Add(text);
                }
            }
        }

        var storedVersion = 1;
        if (document.TryGetPropertyValue("settingsVersion", out var versionNode) &&
            versionNode is JsonValue versionValue &&
            versionValue.TryGetValue<int>(out var parsedVersion))
        {
            storedVersion = parsedVersion;
        }
        else if (values.Count == 0)
        {
            // A fresh store starts at the schema version; there is nothing to migrate.
            storedVersion = _schema.Version;
        }

        var migrated = await MigrateAsync(values, markers, storedVersion, cancellationToken)
            .ConfigureAwait(false);
        _values = values;
        _secretMarkers = markers;
        _storedVersion = migrated;
        if (migrated != storedVersion)
        {
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        return (_values, _secretMarkers);
    }

    /// <summary>
    /// Applies the schema-declared migrations from the stored version to the
    /// schema version. Renames move values (and DPAPI secrets) to the new key,
    /// defaults fill absent keys, and listed keys are removed. Unknown keys are
    /// never touched.
    /// </summary>
    private async Task<int> MigrateAsync(
        JsonObject values,
        HashSet<string> markers,
        int storedVersion,
        CancellationToken cancellationToken)
    {
        var version = storedVersion;
        var changed = false;
        foreach (var step in _schema.Migrations.OrderBy(static migration => migration.FromVersion))
        {
            if (step.FromVersion < version)
            {
                continue;
            }

            foreach (var (oldKey, newKey) in step.Renames)
            {
                var field = _schema.Find(newKey);
                if (field is { Type: PluginSettingsFieldType.Secret })
                {
                    var moved = await MoveSecretAsync(oldKey, newKey, markers, cancellationToken)
                        .ConfigureAwait(false);
                    changed |= moved;
                    continue;
                }

                if (values.TryGetPropertyValue(oldKey, out var movedValue) &&
                    !values.ContainsKey(newKey))
                {
                    values[newKey] = movedValue?.DeepClone();
                    values.Remove(oldKey);
                    changed = true;
                }
            }

            foreach (var (key, defaultValue) in step.Defaults)
            {
                if (!values.ContainsKey(key))
                {
                    values[key] = defaultValue?.DeepClone();
                    changed = true;
                }
            }

            foreach (var key in step.Removed)
            {
                changed |= values.Remove(key);
            }

            version = Math.Max(version, step.FromVersion + 1);
        }

        version = Math.Max(version, _schema.Version);
        return changed ? version : Math.Max(version, storedVersion);
    }

    /// <summary>
    /// Moves a secret from one field key to another during a migration. Only the
    /// DPAPI secret store and the presence marker change; no value ever passes
    /// through the settings file.
    /// </summary>
    private async Task<bool> MoveSecretAsync(
        string oldKey,
        string newKey,
        HashSet<string> markers,
        CancellationToken cancellationToken)
    {
        if (!markers.Remove(oldKey))
        {
            return false;
        }

        var value = await _secrets.GetAsync(SecretName(oldKey), cancellationToken).ConfigureAwait(false);
        await _secrets.RemoveAsync(SecretName(oldKey), cancellationToken).ConfigureAwait(false);
        if (value is not null)
        {
            await _secrets.SetAsync(SecretName(newKey), value, cancellationToken).ConfigureAwait(false);
        }

        markers.Add(newKey);
        return true;
    }

    private async Task<JsonObject> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new JsonObject();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(json, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Plugin settings file '{Path}' is not valid JSON; starting with empty settings.",
                _filePath);
            QuarantineCorruptFile();
            return new JsonObject();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Plugin settings file '{Path}' could not be read; starting with empty settings.",
                _filePath);
            return new JsonObject();
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var secrets = new JsonArray();
        foreach (var name in _secretMarkers!.OrderBy(static candidate => candidate, StringComparer.Ordinal))
        {
            secrets.Add((JsonNode?)name);
        }

        var document = new JsonObject
        {
            ["settingsVersion"] = _storedVersion,
            ["values"] = _values!.DeepClone(),
            ["secrets"] = secrets,
        };
        var json = document.ToJsonString(DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(_filePath, json, keepBackup: true, cancellationToken).ConfigureAwait(false);
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            var quarantinedPath = CorruptFileQuarantine.Quarantine(
                _filePath,
                Path.Combine(directory, "quarantine"),
                _timeProvider.GetUtcNow());
            _logger.LogWarning("Moved corrupt plugin settings to '{Path}'.", quarantinedPath);
        }
        catch (IOException)
        {
            // Keeping the corrupt file in place is preferable to losing it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: leave the file alone.
        }
    }

    private static string BuildSecretName(string pluginId, string fieldKey)
    {
        var name = $"{pluginId}.{fieldKey}";
        if (name.Length > 128 ||
            !name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "The plugin id and field key must form a secret name of 1-128 characters of " +
                    "letters, digits, dots, underscores, or hyphens.",
                nameof(fieldKey));
        }

        return name;
    }
}
