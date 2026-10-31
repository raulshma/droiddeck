using System.Text;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;

namespace DroidDeck.Core.Configuration;

/// <summary>How an imported settings document combines with the current layer.</summary>
public enum SettingsImportMode
{
    /// <summary>Only the values present in the document are written.</summary>
    Merge,

    /// <summary>The layer is replaced by the document, so absent values are removed.</summary>
    Replace,
}

/// <summary>The kind of difference an import would make.</summary>
public enum SettingsChangeKind
{
    /// <summary>The value does not exist in the target layer yet.</summary>
    Added,

    /// <summary>The value exists with a different value.</summary>
    Changed,

    /// <summary>The value exists in the target layer but not in the document.</summary>
    Removed,
}

/// <summary>How serious a validation finding is.</summary>
public enum SettingsIssueSeverity
{
    /// <summary>The document is still accepted.</summary>
    Warning,

    /// <summary>The document is rejected.</summary>
    Error,
}

/// <summary>One finding from validating a settings document.</summary>
/// <param name="Path">The dotted configuration path the finding applies to.</param>
/// <param name="Message">The user-facing explanation.</param>
/// <param name="Severity">Whether the finding blocks the document.</param>
public sealed record SettingsValidationIssue(string Path, string Message, SettingsIssueSeverity Severity);

/// <summary>The result of validating a settings document.</summary>
/// <param name="Issues">Every finding, ordered as discovered.</param>
public sealed record SettingsValidationResult(IReadOnlyList<SettingsValidationIssue> Issues)
{
    /// <summary>Gets a result with no findings.</summary>
    public static SettingsValidationResult Valid { get; } = new([]);

    /// <summary>Gets whether no error-level finding was reported.</summary>
    public bool IsValid => !Issues.Any(static issue => issue.Severity == SettingsIssueSeverity.Error);
}

/// <summary>One difference between a document and a settings layer.</summary>
/// <param name="Path">The dotted configuration path.</param>
/// <param name="Kind">What would happen to the value.</param>
/// <param name="OldValue">The value in the layer, if one exists.</param>
/// <param name="NewValue">The imported value, if one exists.</param>
public sealed record SettingsChange(string Path, SettingsChangeKind Kind, JsonNode? OldValue, JsonNode? NewValue);

/// <summary>What an import would change before it is applied.</summary>
public sealed record SettingsImportPreview
{
    /// <summary>Gets the target layer.</summary>
    public required ConfigScope Scope { get; init; }

    /// <summary>Gets the merge mode.</summary>
    public required SettingsImportMode Mode { get; init; }

    /// <summary>Gets the document validation result.</summary>
    public required SettingsValidationResult Validation { get; init; }

    /// <summary>Gets the changes the import would make, ordered by path.</summary>
    public required IReadOnlyList<SettingsChange> Changes { get; init; }

    /// <summary>Gets the sensitive paths that were removed from the document.</summary>
    public required IReadOnlyList<string> ExcludedPaths { get; init; }

    /// <summary>Gets whether the import would change anything.</summary>
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>The applied result of an import.</summary>
/// <param name="Preview">The preview that was applied.</param>
/// <param name="BackupPath">The backup of the previous layer contents, when one was kept.</param>
public sealed record SettingsImportResult(SettingsImportPreview Preview, string? BackupPath);

/// <summary>Selects the layers to include in an export.</summary>
public sealed record SettingsExportOptions
{
    /// <summary>Gets the layers to export; defaults to every persistable active layer.</summary>
    public IReadOnlyList<ConfigScope>? Scopes { get; init; }
}

/// <summary>A portable copy of one or more settings layers without secrets.</summary>
public sealed record SettingsExport
{
    /// <summary>Gets when the export was produced.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the exported layer documents by scope.</summary>
    public required IReadOnlyDictionary<ConfigScope, JsonObject> Scopes { get; init; }

    /// <summary>Gets the sensitive paths that were omitted.</summary>
    public required IReadOnlyList<string> ExcludedPaths { get; init; }

    /// <summary>Gets the layers exported by default.</summary>
    public static IReadOnlyList<ConfigScope> DefaultScopes { get; } =
    [
        ConfigScope.Global,
        ConfigScope.Profile,
        ConfigScope.TeamProject,
        ConfigScope.LocalProject,
        ConfigScope.RunConfiguration,
    ];

    /// <summary>Serializes the export bundle.</summary>
    public string ToJson()
    {
        var scopes = new JsonObject();
        foreach (var pair in Scopes)
        {
            scopes[ScopeName(pair.Key)] = pair.Value.DeepClone();
        }

        var bundle = new JsonObject
        {
            ["formatVersion"] = 1,
            ["generatedAt"] = GeneratedAt,
            ["scopes"] = scopes,
        };
        return bundle.ToJsonString(DroidDeckJson.Options);
    }

    internal static string ScopeName(ConfigScope scope) => scope switch
    {
        ConfigScope.BuiltInDefaults => "builtInDefaults",
        ConfigScope.Global => "global",
        ConfigScope.Profile => "profile",
        ConfigScope.TeamProject => "teamProject",
        ConfigScope.LocalProject => "localProject",
        ConfigScope.RunConfiguration => "runConfiguration",
        ConfigScope.Device => "device",
        ConfigScope.Session => "session",
        _ => scope.ToString(),
    };
}

/// <summary>The layer contents captured before a reset so the reset can be undone.</summary>
public sealed record SettingsResetSnapshot
{
    /// <summary>Gets the reset layer.</summary>
    public required ConfigScope Scope { get; init; }

    /// <summary>Gets the layer contents before the reset.</summary>
    public required JsonObject Document { get; init; }

    /// <summary>Gets the values that were removed.</summary>
    public required IReadOnlyList<SettingsChange> Removed { get; init; }
}

/// <summary>Thrown when an imported document fails validation and is not applied.</summary>
public sealed class SettingsValidationException : Exception
{
    /// <summary>Creates the exception for a failed validation result.</summary>
    public SettingsValidationException(SettingsValidationResult result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    /// <summary>Gets the validation findings.</summary>
    public SettingsValidationResult Result { get; }

    private static string BuildMessage(SettingsValidationResult result)
    {
        var first = result.Issues.FirstOrDefault(static issue => issue.Severity == SettingsIssueSeverity.Error);
        return first is null
            ? "The settings document is invalid."
            : $"The settings document is invalid at {first.Path}: {first.Message}";
    }
}

/// <summary>Shared JSON helpers for exporting, importing, and validating settings.</summary>
internal static class SettingsTransfer
{
    private static readonly string[] SensitiveMarkers =
    [
        "secret", "token", "password", "credential", "apikey", "privatekey",
    ];

    public static bool IsSensitive(string path)
    {
        foreach (var segment in path.Split('.'))
        {
            var normalized = Normalize(segment);
            foreach (var marker in SensitiveMarkers)
            {
                if (normalized.Contains(marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Returns a copy without sensitive paths and records what was removed.</summary>
    public static JsonObject Sanitize(JsonObject document, ICollection<string> excluded)
    {
        var copy = (JsonObject)document.DeepClone();
        RemoveSensitive(copy, string.Empty, excluded);
        return copy;
    }

    /// <summary>
    /// Copies sensitive values from the source into the target. Replace imports never
    /// carry secrets, so keeping the existing ones avoids deleting them silently.
    /// </summary>
    public static void PreserveSensitive(JsonObject source, JsonObject target)
    {
        foreach (var pair in SettingsDocument.Flatten(source))
        {
            if (IsSensitive(pair.Key))
            {
                SettingsDocument.Set(target, pair.Key, pair.Value);
            }
        }
    }

    public static Dictionary<string, JsonNode?> Flatten(JsonObject document) =>
        SettingsDocument.Flatten(document);

    public static void Merge(JsonObject target, JsonObject overlay) =>
        SettingsDocument.Merge(target, overlay);

    public static IReadOnlyList<SettingsChange> Diff(
        Dictionary<string, JsonNode?> current,
        Dictionary<string, JsonNode?> imported,
        SettingsImportMode mode)
    {
        var changes = new List<SettingsChange>();
        foreach (var pair in imported)
        {
            if (IsSchemaVersion(pair.Key))
            {
                continue;
            }

            if (!current.TryGetValue(pair.Key, out var oldValue))
            {
                changes.Add(new SettingsChange(pair.Key, SettingsChangeKind.Added, null, pair.Value));
            }
            else if (!JsonNode.DeepEquals(oldValue, pair.Value))
            {
                changes.Add(new SettingsChange(pair.Key, SettingsChangeKind.Changed, oldValue, pair.Value));
            }
        }

        if (mode == SettingsImportMode.Replace)
        {
            foreach (var pair in current)
            {
                if (IsSchemaVersion(pair.Key) || imported.ContainsKey(pair.Key) || IsSensitive(pair.Key))
                {
                    continue;
                }

                changes.Add(new SettingsChange(pair.Key, SettingsChangeKind.Removed, pair.Value, null));
            }
        }

        changes.Sort(static (left, right) =>
            string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return changes;
    }

    public static void CollectCaseConflicts(JsonNode? node, string prefix, List<SettingsValidationIssue> issues)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in obj)
        {
            if (seen.TryGetValue(property.Key, out var existing) &&
                !string.Equals(existing, property.Key, StringComparison.Ordinal))
            {
                issues.Add(new SettingsValidationIssue(
                    SettingsDocument.Combine(prefix, existing),
                    $"Duplicate key \"{property.Key}\" differs only by case from \"{existing}\". Settings keys must be unique ignoring case.",
                    SettingsIssueSeverity.Error));
            }
            else
            {
                seen[property.Key] = property.Key;
            }

            CollectCaseConflicts(property.Value, SettingsDocument.Combine(prefix, property.Key), issues);
        }
    }

    public static void CollectUnknownKeys(JsonObject document, List<SettingsValidationIssue> issues)
    {
        foreach (var path in SettingsDocument.Flatten(document).Keys)
        {
            if (IsSchemaVersion(path) || SettingsCatalog.Default.Find(path) is not null)
            {
                continue;
            }

            issues.Add(new SettingsValidationIssue(
                path,
                $"\"{path}\" is not a setting this version understands. It is preserved so newer builds can read it.",
                SettingsIssueSeverity.Warning));
        }
    }

    public static JsonObject? UnwrapBundle(JsonObject document, ConfigScope scope, out string? error)
    {
        if (SettingsDocument.FindKey(document, "scopes") is not { } scopesKey || document[scopesKey] is not JsonObject scopes)
        {
            error = null;
            return document;
        }

        var scopeName = SettingsExport.ScopeName(scope);
        var scopeKey = SettingsDocument.FindKey(scopes, scopeName);
        if (scopeKey is null)
        {
            error = $"The export does not contain the {scopeName} scope.";
            return null;
        }

        if (scopes[scopeKey] is not JsonObject scopeDocument)
        {
            error = $"The exported {scopeName} scope is not a JSON object.";
            return null;
        }

        error = null;
        return (JsonObject)scopeDocument.DeepClone();
    }

    public static string NormalizeJsonPath(string? jsonPath)
    {
        if (string.IsNullOrEmpty(jsonPath) || string.Equals(jsonPath, "$", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var start = jsonPath.StartsWith("$.", StringComparison.Ordinal)
            ? 2
            : jsonPath.StartsWith('$') ? 1 : 0;
        var builder = new StringBuilder(jsonPath.Length);
        var depth = 0;
        for (var index = start; index < jsonPath.Length; index++)
        {
            var character = jsonPath[index];
            if (character == '[')
            {
                depth++;
            }
            else if (character == ']')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0)
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsSchemaVersion(string path) =>
        string.Equals(path, "schemaVersion", StringComparison.OrdinalIgnoreCase);

    private static void RemoveSensitive(JsonObject obj, string prefix, ICollection<string> excluded)
    {
        foreach (var key in obj.Select(static property => property.Key).ToArray())
        {
            var path = SettingsDocument.Combine(prefix, key);
            if (IsSensitive(path))
            {
                obj.Remove(key);
                excluded.Add(path);
                continue;
            }

            if (obj[key] is JsonObject child)
            {
                RemoveSensitive(child, path, excluded);
            }
        }
    }

    private static string Normalize(string segment)
    {
        var builder = new StringBuilder(segment.Length);
        foreach (var character in segment)
        {
            if (character is not ('_' or '-'))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
