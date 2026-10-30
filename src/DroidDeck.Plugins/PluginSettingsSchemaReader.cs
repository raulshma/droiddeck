using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// The outcome of reading a plugin <c>settings.schema.json</c>.
/// </summary>
/// <param name="Schema">The schema, or <see langword="null"/> when it could not be read.</param>
/// <param name="Validation">The issues found while reading the schema.</param>
public sealed record PluginSettingsSchemaReadResult(PluginSettingsSchema? Schema, PluginValidationResult Validation)
{
    /// <summary>Gets a value indicating whether a usable schema was read.</summary>
    public bool Succeeded => Schema is not null;
}

/// <summary>
/// Reads the safe JSON Schema subset that plugin <c>settings.schema.json</c> files
/// use: <c>version</c>, <c>title</c>, <c>description</c>, <c>properties</c>,
/// <c>required</c>, and <c>migrations</c>, with fields typed as string, number,
/// integer, boolean, enum (a string with <c>enum</c>), path and secret (strings
/// with a <c>format</c>), list (array), and one level of nested object fields.
/// Anything outside the subset is rejected with a clear issue so plugins fail
/// visibly instead of getting a settings UI they did not expect.
/// </summary>
public static class PluginSettingsSchemaReader
{
    /// <summary>The schema file name expected beside <c>plugin.json</c>.</summary>
    public const string FileName = "settings.schema.json";

    /// <summary>Gets the schema path for a plugin directory.</summary>
    public static string GetSchemaPath(string pluginDirectory)
        => Path.Combine(pluginDirectory, FileName);

    private static readonly string[] RecognizedKeys = ["version", "title", "description", "properties", "required", "migrations"];

    private static readonly string[] RecognizedFieldKeys =
    [
        "type", "title", "description", "default", "enum", "minimum", "maximum",
        "minLength", "maxLength", "pattern", "format", "items", "properties", "required",
    ];

    private static readonly string[] ScalarItemTypes = ["string", "number", "integer", "boolean"];

    // Settings documents keep housekeeping keys at the document root, so field
    // keys must never collide with them.
    private static readonly string[] ReservedKeys = ["settingsVersion", "values", "secrets"];

    /// <summary>
    /// Reads and validates the schema in a plugin directory. A missing schema file
    /// is not an error: plugins without one keep the raw settings store.
    /// </summary>
    public static async Task<PluginSettingsSchemaReadResult> ReadAsync(
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var validation = new PluginValidationResult();
        var schemaPath = GetSchemaPath(pluginDirectory);
        if (!File.Exists(schemaPath))
        {
            return new PluginSettingsSchemaReadResult(null, validation);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(schemaPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            validation.Error("settingsSchema.unreadable", $"Could not read '{schemaPath}': {exception.Message}");
            return new PluginSettingsSchemaReadResult(null, validation);
        }

        JsonObject document;
        try
        {
            document = JsonNode.Parse(json, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
                ?? throw new JsonException("The schema is not a JSON object.");
        }
        catch (JsonException exception)
        {
            validation.Error("settingsSchema.invalidJson", $"Could not parse '{schemaPath}': {exception.Message}");
            return new PluginSettingsSchemaReadResult(null, validation);
        }

        var schema = Parse(document, validation);
        return new PluginSettingsSchemaReadResult(schema, validation);
    }

    private static PluginSettingsSchema? Parse(JsonObject document, PluginValidationResult validation)
    {
        foreach (var key in document.Select(static property => property.Key))
        {
            if (!RecognizedKeys.Contains(key, StringComparer.Ordinal))
            {
                validation.Error(
                    "settingsSchema.unknownKey",
                    $"The schema key '{key}' is not part of the supported settings schema subset. " +
                        "Supported keys: " + string.Join(", ", RecognizedKeys) + ".");
            }
        }

        var version = 1;
        if (document.TryGetPropertyValue("version", out var versionNode) && versionNode is not null)
        {
            if (versionNode is JsonValue value && value.TryGetValue<int>(out var parsed) &&
                parsed is >= 1 and <= 1_000_000)
            {
                version = parsed;
            }
            else
            {
                validation.Error(
                    "settingsSchema.version.invalid",
                    "The schema 'version' must be an integer between 1 and 1000000.");
            }
        }

        var title = OptionalString(document, "title");
        var description = OptionalString(document, "description");

        var fields = new List<PluginSettingsField>();
        if (document.TryGetPropertyValue("properties", out var properties) && properties is not null)
        {
            if (properties is not JsonObject propertiesObject)
            {
                validation.Error("settingsSchema.properties.invalid", "The schema 'properties' must be a JSON object.");
            }
            else
            {
                fields.AddRange(ParseFields(propertiesObject, nested: false, validation));
            }
        }

        var required = ParseRequired(document, fields, validation);
        foreach (var field in fields)
        {
            if (required.Contains(field.Key, StringComparer.Ordinal))
            {
                field.Required = true;
            }
        }

        var migrations = ParseMigrations(document, version, validation);

        return validation.IsValid
            ? new PluginSettingsSchema
            {
                Version = version,
                Title = title,
                Description = description,
                Fields = fields,
                Migrations = migrations,
            }
            : null;
    }

    private static List<PluginSettingsField> ParseFields(
        JsonObject properties,
        bool nested,
        PluginValidationResult validation)
    {
        var fields = new List<PluginSettingsField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, definition) in properties)
        {
            ValidateKey(key, nested, seen, validation);
            if (definition is not JsonObject fieldObject)
            {
                validation.Error(
                    "settingsSchema.field.invalid",
                    $"The definition of '{key}' must be a JSON object.");
                continue;
            }

            var field = ParseField(key, fieldObject, nested, validation);
            if (field is not null)
            {
                fields.Add(field);
            }
        }

        return fields;
    }

    private static PluginSettingsField? ParseField(
        string key,
        JsonObject definition,
        bool nested,
        PluginValidationResult validation)
    {
        foreach (var definitionKey in definition.Select(static property => property.Key))
        {
            if (!RecognizedFieldKeys.Contains(definitionKey, StringComparer.Ordinal))
            {
                validation.Error(
                    "settingsSchema.unknownKey",
                    $"The field '{key}' uses '{definitionKey}', which is not part of the supported " +
                        "settings schema subset. Supported keys: " + string.Join(", ", RecognizedFieldKeys) + ".");
            }
        }

        var typeName = OptionalString(definition, "type") ?? "string";
        var format = OptionalString(definition, "format");
        var type = typeName switch
        {
            "string" when string.Equals(format, "secret", StringComparison.OrdinalIgnoreCase) =>
                PluginSettingsFieldType.Secret,
            "string" when string.Equals(format, "path", StringComparison.OrdinalIgnoreCase) =>
                PluginSettingsFieldType.Path,
            "string" => PluginSettingsFieldType.Text,
            "number" => PluginSettingsFieldType.Number,
            "integer" => PluginSettingsFieldType.WholeNumber,
            "boolean" => PluginSettingsFieldType.Boolean,
            "array" => PluginSettingsFieldType.List,
            "object" => PluginSettingsFieldType.Group,
            _ => PluginSettingsFieldType.Text,
        };

        if (typeName is not ("string" or "number" or "integer" or "boolean" or "array" or "object"))
        {
            validation.Error(
                "settingsSchema.field.invalid",
                $"The field '{key}' has an unsupported type '{typeName}'. Supported types: " +
                    "string, number, integer, boolean, array, object.");
        }

        if (!string.IsNullOrEmpty(format) &&
            !string.Equals(format, "path", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(format, "secret", StringComparison.OrdinalIgnoreCase))
        {
            validation.Error(
                "settingsSchema.field.invalid",
                $"The field '{key}' has an unsupported format '{format}'. Supported formats: path, secret.");
        }

        if (format is not null && typeName != "string")
        {
            validation.Error(
                "settingsSchema.field.invalid",
                $"The field '{key}' declares a 'format', which is only valid for string fields.");
        }

        var enumValues = ParseEnum(key, typeName, definition, validation);
        if (enumValues is not null)
        {
            if (format is not null)
            {
                // An enum field with a format would silently lose the format:
                // a secret enum would fall back to plaintext storage. Reject it
                // so the plugin author sees the conflict at parse time.
                validation.Error(
                    "settingsSchema.field.invalid",
                    $"The field '{key}' declares both a 'format' and an 'enum', which cannot be combined. " +
                        "Remove the enum or the format.");
                enumValues = null;
            }
            else
            {
                type = PluginSettingsFieldType.Enum;
            }
        }

        var pattern = OptionalString(definition, "pattern");
        Regex? patternRegex = null;
        if (pattern is not null)
        {
            try
            {
                patternRegex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException exception)
            {
                validation.Error(
                    "settingsSchema.pattern.invalid",
                    $"The field '{key}' has an invalid pattern: {exception.Message}");
            }
        }

        var minimum = OptionalNumber(definition, "minimum", key, validation);
        var maximum = OptionalNumber(definition, "maximum", key, validation);
        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            validation.Error(
                "settingsSchema.field.invalid",
                $"The field '{key}' declares a minimum above its maximum.");
        }

        var minLength = OptionalInteger(definition, "minLength", key, validation);
        var maxLength = OptionalInteger(definition, "maxLength", key, validation);

        var itemType = PluginSettingsFieldType.Text;
        if (definition.TryGetPropertyValue("items", out var items) && items is not null)
        {
            if (typeName != "array")
            {
                validation.Error(
                    "settingsSchema.items.invalid",
                    $"The field '{key}' declares 'items', which is only valid for array fields.");
            }
            else if (items is JsonObject itemsObject &&
                itemsObject.Count == 1 &&
                itemsObject.TryGetPropertyValue("type", out var itemTypeNode) &&
                itemTypeNode is JsonValue itemTypeValue &&
                itemTypeValue.TryGetValue<string>(out var itemTypeName) &&
                ScalarItemTypes.Contains(itemTypeName, StringComparer.Ordinal))
            {
                itemType = itemTypeName switch
                {
                    "number" => PluginSettingsFieldType.Number,
                    "integer" => PluginSettingsFieldType.WholeNumber,
                    "boolean" => PluginSettingsFieldType.Boolean,
                    _ => PluginSettingsFieldType.Text,
                };
            }
            else
            {
                validation.Error(
                    "settingsSchema.items.invalid",
                    $"The field '{key}' must declare items as {{ \"type\": \"string|number|integer|boolean\" }}.");
            }
        }

        List<PluginSettingsField>? nestedFields = null;
        if (definition.TryGetPropertyValue("properties", out var nestedProperties) && nestedProperties is not null)
        {
            if (typeName != "object")
            {
                validation.Error(
                    "settingsSchema.properties.invalid",
                    $"The field '{key}' declares 'properties', which is only valid for object fields.");
            }
            else if (nested)
            {
                validation.Error(
                    "settingsSchema.properties.invalid",
                    $"The object field '{key}' declares nested object fields, but object fields may not nest deeper than one level.");
            }
            else if (nestedProperties is JsonObject nestedObject)
            {
                nestedFields = ParseFields(nestedObject, nested: true, validation);
                foreach (var nestedKey in ParseRequired(definition, nestedFields, validation))
                {
                    var match = nestedFields.Find(candidate => candidate.Key == nestedKey);
                    if (match is not null)
                    {
                        match.Required = true;
                    }
                }
            }
            else
            {
                validation.Error(
                    "settingsSchema.properties.invalid",
                    $"The 'properties' of object field '{key}' must be a JSON object.");
            }
        }

        JsonNode? defaultValue = null;
        if (definition.TryGetPropertyValue("default", out var defaultNode) && defaultNode is not null)
        {
            defaultValue = defaultNode.DeepClone();
        }

        var field = new PluginSettingsField
        {
            Key = key,
            Type = type,
            Title = OptionalString(definition, "title"),
            Description = OptionalString(definition, "description"),
            Default = defaultValue,
            EnumValues = enumValues ?? [],
            Minimum = minimum,
            Maximum = maximum,
            MinLength = minLength,
            MaxLength = maxLength,
            Pattern = pattern,
            PatternRegex = patternRegex,
            ItemType = itemType,
            Properties = nestedFields ?? [],
        };

        // A default that violates the schema would make every stored document
        // invalid, so it is rejected here where the plugin author can see it.
        if (defaultValue is not null)
        {
            foreach (var issue in PluginSettingsValidator.Validate(field, defaultValue))
            {
                validation.Error(
                    "settingsSchema.default.invalid",
                    $"The default of field '{key}' is invalid: {issue.Message}");
            }
        }

        return field;
    }

    private static List<string>? ParseEnum(
        string key,
        string typeName,
        JsonObject definition,
        PluginValidationResult validation)
    {
        if (!definition.TryGetPropertyValue("enum", out var enumNode) || enumNode is null)
        {
            return null;
        }

        if (typeName != "string")
        {
            validation.Error(
                "settingsSchema.enum.invalid",
                $"The field '{key}' declares 'enum', which is only valid for string fields.");
            return null;
        }

        if (enumNode is not JsonArray array || array.Count == 0)
        {
            validation.Error(
                "settingsSchema.enum.invalid",
                $"The field '{key}' must declare 'enum' as a non-empty array of strings.");
            return null;
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                validation.Error(
                    "settingsSchema.enum.invalid",
                    $"The field '{key}' must declare 'enum' as a non-empty array of strings.");
                return null;
            }

            values.Add(text);
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            validation.Error(
                "settingsSchema.enum.invalid",
                $"The field '{key}' declares duplicate enum values.");
            return null;
        }

        return values;
    }

    private static List<string> ParseRequired(
        JsonObject definition,
        List<PluginSettingsField> fields,
        PluginValidationResult validation)
    {
        var required = new List<string>();
        if (!definition.TryGetPropertyValue("required", out var requiredNode) || requiredNode is null)
        {
            return required;
        }

        if (requiredNode is not JsonArray array)
        {
            validation.Error(
                "settingsSchema.required.invalid",
                "The 'required' list must be an array of field names.");
            return required;
        }

        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var name))
            {
                validation.Error(
                    "settingsSchema.required.invalid",
                    "The 'required' list must be an array of field names.");
                return required;
            }

            if (!fields.Exists(candidate => string.Equals(candidate.Key, name, StringComparison.Ordinal)))
            {
                validation.Error(
                    "settingsSchema.required.invalid",
                    $"The 'required' list names '{name}', which is not a declared field.");
                continue;
            }

            required.Add(name);
        }

        return required;
    }

    private static List<PluginSettingsSchemaMigration> ParseMigrations(
        JsonObject document,
        int schemaVersion,
        PluginValidationResult validation)
    {
        var migrations = new List<PluginSettingsSchemaMigration>();
        if (!document.TryGetPropertyValue("migrations", out var migrationsNode) || migrationsNode is null)
        {
            return migrations;
        }

        if (migrationsNode is not JsonArray array)
        {
            validation.Error(
                "settingsSchema.migrations.invalid",
                "The 'migrations' list must be an array of migration steps.");
            return migrations;
        }

        var seenVersions = new HashSet<int>();
        foreach (var item in array)
        {
            if (item is not JsonObject step)
            {
                validation.Error(
                    "settingsSchema.migrations.invalid",
                    "Each migration step must be a JSON object with a 'fromVersion'.");
                continue;
            }

            foreach (var stepKey in step.Select(static property => property.Key))
            {
                if (stepKey is not ("fromVersion" or "renames" or "defaults" or "removed"))
                {
                    validation.Error(
                        "settingsSchema.migrations.invalid",
                        $"The migration key '{stepKey}' is not supported. Supported keys: " +
                            "fromVersion, renames, defaults, removed.");
                }
            }

            var fromVersion = 0;
            if (step.TryGetPropertyValue("fromVersion", out var fromNode) &&
                fromNode is JsonValue fromValue &&
                fromValue.TryGetValue<int>(out var parsedFrom))
            {
                fromVersion = parsedFrom;
            }

            if (fromVersion is < 1 or > 1_000_000)
            {
                validation.Error(
                    "settingsSchema.migrations.invalid",
                    "Each migration step must declare a 'fromVersion' between 1 and 1000000.");
                continue;
            }

            if (!seenVersions.Add(fromVersion))
            {
                validation.Error(
                    "settingsSchema.migrations.invalid",
                    $"Two migration steps declare the same fromVersion {fromVersion}.");
                continue;
            }

            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            if (step.TryGetPropertyValue("renames", out var renamesNode) && renamesNode is not null)
            {
                if (renamesNode is JsonObject renamesObject)
                {
                    foreach (var (oldKey, target) in renamesObject)
                    {
                        if (target is JsonValue targetValue && targetValue.TryGetValue<string>(out var newKey))
                        {
                            renames[oldKey] = newKey;
                        }
                        else
                        {
                            validation.Error(
                                "settingsSchema.migrations.invalid",
                                $"The rename '{oldKey}' must map to a string key.");
                        }
                    }
                }
                else
                {
                    validation.Error(
                        "settingsSchema.migrations.invalid",
                        "The migration 'renames' must be an object mapping old keys to new keys.");
                }
            }

            var defaults = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (step.TryGetPropertyValue("defaults", out var defaultsNode) && defaultsNode is not null)
            {
                if (defaultsNode is JsonObject defaultsObject)
                {
                    foreach (var (key, value) in defaultsObject)
                    {
                        defaults[key] = value?.DeepClone();
                    }
                }
                else
                {
                    validation.Error(
                        "settingsSchema.migrations.invalid",
                        "The migration 'defaults' must be an object mapping keys to values.");
                }
            }

            var removed = new List<string>();
            if (step.TryGetPropertyValue("removed", out var removedNode) && removedNode is not null)
            {
                if (removedNode is JsonArray removedArray)
                {
                    foreach (var key in removedArray)
                    {
                        if (key is JsonValue keyValue && keyValue.TryGetValue<string>(out var removedKey))
                        {
                            removed.Add(removedKey);
                        }
                        else
                        {
                            validation.Error(
                                "settingsSchema.migrations.invalid",
                                "The migration 'removed' list must contain string keys.");
                        }
                    }
                }
                else
                {
                    validation.Error(
                        "settingsSchema.migrations.invalid",
                        "The migration 'removed' must be an array of keys.");
                }
            }

            migrations.Add(new PluginSettingsSchemaMigration
            {
                FromVersion = fromVersion,
                Renames = renames,
                Defaults = defaults,
                Removed = removed,
            });
        }

        if (validation.IsValid && migrations.Exists(step => step.FromVersion >= schemaVersion))
        {
            validation.Error(
                "settingsSchema.migrations.invalid",
                "A migration step upgrades from a version that is not older than the schema version.");
        }

        return migrations;
    }

    private static void ValidateKey(string key, bool nested, HashSet<string> seen, PluginValidationResult validation)
    {
        var scope = nested ? "Nested field" : "Field";
        if (key.Length is 0 or > 64 || !key.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            validation.Error(
                "settingsSchema.key.invalid",
                $"The {scope} key '{key}' must be 1-64 characters of letters, digits, dots, underscores, or hyphens.");
            return;
        }

        if (!nested && ReservedKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            validation.Error(
                "settingsSchema.key.reserved",
                $"The field key '{key}' is reserved by the settings document format.");
            return;
        }

        if (!seen.Add(key))
        {
            validation.Error(
                "settingsSchema.key.duplicate",
                $"The {scope} key '{key}' is declared more than once, ignoring case.");
        }
    }

    private static string? OptionalString(JsonObject definition, string key)
        => definition.TryGetPropertyValue(key, out var node) && node is JsonValue value &&
            value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static double? OptionalNumber(
        JsonObject definition,
        string key,
        string fieldKey,
        PluginValidationResult validation)
    {
        if (!definition.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<double>(out var number))
        {
            return number;
        }

        validation.Error(
            "settingsSchema.field.invalid",
            $"The field '{fieldKey}' must declare '{key}' as a number.");
        return null;
    }

    private static int? OptionalInteger(
        JsonObject definition,
        string key,
        string fieldKey,
        PluginValidationResult validation)
    {
        if (!definition.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0)
        {
            return number;
        }

        validation.Error(
            "settingsSchema.field.invalid",
            $"The field '{fieldKey}' must declare '{key}' as a non-negative integer.");
        return null;
    }
}
