using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DroidDeck.Plugin.Abstractions;

/// <summary>The value shape of a plugin settings field.</summary>
public enum PluginSettingsFieldType
{
    /// <summary>A free-form string.</summary>
    Text,

    /// <summary>A decimal number.</summary>
    Number,

    /// <summary>A whole number.</summary>
    WholeNumber,

    /// <summary>A boolean toggle.</summary>
    Boolean,

    /// <summary>One of a fixed set of strings.</summary>
    Enum,

    /// <summary>A file or directory path.</summary>
    Path,

    /// <summary>A value that the host stores encrypted and never displays.</summary>
    Secret,

    /// <summary>A list of scalar values.</summary>
    List,

    /// <summary>A group of named sub-fields, one level deep.</summary>
    Group,
}

/// <summary>
/// One field of a plugin settings schema. The shape mirrors the safe JSON Schema
/// subset that <c>settings.schema.json</c> uses: <c>type</c>, <c>format</c>,
/// <c>enum</c>, <c>default</c>, numeric and length bounds, <c>pattern</c>, list
/// <c>items</c>, and one level of nested object <c>properties</c>.
/// </summary>
public sealed class PluginSettingsField
{
    /// <summary>Gets the settings key the field is stored under.</summary>
    public required string Key { get; init; }

    /// <summary>Gets the value shape of the field.</summary>
    public required PluginSettingsFieldType Type { get; init; }

    /// <summary>Gets the user-facing name, when the schema declares one.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the user-facing explanation, when the schema declares one.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the value applied when the setting is absent, when declared.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Gets or sets whether the setting must have a value. The schema reader
    /// sets this from the schema's <c>required</c> list after parsing.</summary>
    public bool Required { get; set; }

    /// <summary>Gets the allowed strings for <see cref="PluginSettingsFieldType.Enum"/> fields.</summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    /// <summary>Gets the inclusive lower bound for numeric fields, when declared.</summary>
    public double? Minimum { get; init; }

    /// <summary>Gets the inclusive upper bound for numeric fields, when declared.</summary>
    public double? Maximum { get; init; }

    /// <summary>Gets the minimum string length, when declared.</summary>
    public int? MinLength { get; init; }

    /// <summary>Gets the maximum string length, when declared.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Gets the regular expression string values must match, when declared.</summary>
    public string? Pattern { get; init; }

    /// <summary>Gets the compiled <see cref="Pattern"/>, when one was declared.</summary>
    public Regex? PatternRegex { get; init; }

    /// <summary>Gets the value shape of list items for <see cref="PluginSettingsFieldType.List"/> fields.</summary>
    public PluginSettingsFieldType ItemType { get; init; } = PluginSettingsFieldType.Text;

    /// <summary>Gets the nested fields of <see cref="PluginSettingsFieldType.Group"/> fields.</summary>
    public IReadOnlyList<PluginSettingsField> Properties { get; init; } = [];

    /// <summary>Gets the user-facing display name: the title when declared, otherwise the key.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? Key : Title;
}

/// <summary>
/// One schema-declared migration step. When stored settings carry an older
/// <c>settingsVersion</c>, steps with a <c>fromVersion</c> at or above the stored
/// version run in order: values are renamed, defaults are filled, and listed keys
/// are removed. Unknown keys are never touched.
/// </summary>
public sealed class PluginSettingsSchemaMigration
{
    /// <summary>Gets the stored settings version this step upgrades.</summary>
    public required int FromVersion { get; init; }

    /// <summary>Gets the old-to-new key renames.</summary>
    public IReadOnlyDictionary<string, string> Renames { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the values to write when the target key is absent.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Defaults { get; init; } =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

    /// <summary>Gets the keys to remove.</summary>
    public IReadOnlyList<string> Removed { get; init; } = [];
}

/// <summary>
/// The schema a plugin ships as <c>settings.schema.json</c> beside its manifest.
/// The host generates the settings UI from it, validates values against it, and
/// uses its <c>version</c> to migrate stored settings.
/// </summary>
public sealed class PluginSettingsSchema
{
    /// <summary>Gets the schema version stored settings migrate to.</summary>
    public required int Version { get; init; }

    /// <summary>Gets the schema title, when the schema declares one.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the schema description, when the schema declares one.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the declared fields in schema order.</summary>
    public required IReadOnlyList<PluginSettingsField> Fields { get; init; }

    /// <summary>Gets the migration steps in declaration order.</summary>
    public IReadOnlyList<PluginSettingsSchemaMigration> Migrations { get; init; } = [];

    /// <summary>Finds a top-level field by key. Lookup is ordinal.</summary>
    public PluginSettingsField? Find(string key)
    {
        foreach (var field in Fields)
        {
            if (string.Equals(field.Key, key, StringComparison.Ordinal))
            {
                return field;
            }
        }

        return null;
    }
}
