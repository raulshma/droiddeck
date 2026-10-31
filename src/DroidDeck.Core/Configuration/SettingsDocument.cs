using System.Text.Json.Nodes;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// Reads, writes, flattens, and merges settings documents by dotted path. Every
/// segment matches case-insensitively and writes reuse the casing already stored
/// in the document.
/// </summary>
internal static class SettingsDocument
{
    /// <summary>Joins a dotted prefix and a name without leaving a leading dot.</summary>
    public static string Combine(string prefix, string name) =>
        prefix.Length == 0 ? name : prefix + "." + name;

    /// <summary>Returns the stored key that matches a name ignoring case, when one exists.</summary>
    public static string? FindKey(JsonObject obj, string name)
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

    /// <summary>Reads a property and its stored key spelling, ignoring case.</summary>
    public static bool TryGetEntry(JsonObject obj, string name, out string key, out JsonNode? value)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                key = property.Key;
                value = property.Value;
                return true;
            }
        }

        key = name;
        value = null;
        return false;
    }

    /// <summary>Reads a property by name, ignoring case.</summary>
    public static bool TryGetProperty(JsonObject obj, string name, out JsonNode? value) =>
        TryGetEntry(obj, name, out _, out value);

    /// <summary>Traverses a dotted path through objects, ignoring the case of every segment.</summary>
    public static bool TryGet(JsonObject root, string dottedPath, out JsonNode? value)
    {
        JsonNode? current = root;
        foreach (var segment in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj || !TryGetProperty(obj, segment, out var next))
            {
                value = null;
                return false;
            }

            current = next;
        }

        value = current;
        return true;
    }

    /// <summary>Reads a dotted path that must resolve to a string value.</summary>
    public static bool TryGetString(JsonObject root, string dottedPath, out string? value)
    {
        if (TryGet(root, dottedPath, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Writes a value at a dotted path, creating missing intermediate objects and
    /// replacing intermediates that are not objects.
    /// </summary>
    public static void Set(JsonObject root, string dottedPath, JsonNode? value)
    {
        var segments = dottedPath.Split('.');
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

        current[FindKey(current, segments[^1]) ?? segments[^1]] = value;
    }

    /// <summary>Removes the exact leaf at a dotted path and returns false when it is absent.</summary>
    public static bool Remove(JsonObject root, string dottedPath)
    {
        var segments = dottedPath.Split('.');
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
        return leaf is not null && current.Remove(leaf);
    }

    /// <summary>
    /// Returns one deep-cloned value per leaf path. Non-empty objects are branches;
    /// every other value, including an empty object, is a leaf.
    /// </summary>
    public static Dictionary<string, JsonNode?> Flatten(JsonObject document)
    {
        var leaves = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        VisitLeaves(document, string.Empty, (path, node) => leaves[path] = node?.DeepClone());
        return leaves;
    }

    /// <summary>
    /// Applies overrides onto a target. Values merge recursively only when both the
    /// existing and incoming values are objects; otherwise the incoming value replaces
    /// the existing one as a deep clone. <paramref name="onReplaced"/> receives each
    /// replaced node path, and <paramref name="onLeaf"/> receives each resulting leaf
    /// path under a replacement, both in document order.
    /// </summary>
    public static void Merge(
        JsonObject target,
        JsonObject overrides,
        string prefix = "",
        Action<string>? onReplaced = null,
        Action<string, JsonNode?>? onLeaf = null)
    {
        foreach (var property in overrides)
        {
            var path = Combine(prefix, property.Key);
            var found = TryGetEntry(target, property.Key, out var key, out var existing);

            if (existing is JsonObject existingObject && property.Value is JsonObject overrideObject)
            {
                Merge(existingObject, overrideObject, path, onReplaced, onLeaf);
                continue;
            }

            var replacement = property.Value?.DeepClone();
            onReplaced?.Invoke(path);
            target[found ? key : property.Key] = replacement;
            if (onLeaf is not null)
            {
                VisitLeaves(replacement, path, onLeaf);
            }
        }
    }

    private static void VisitLeaves(JsonNode? node, string prefix, Action<string, JsonNode?> visit)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var property in obj)
            {
                VisitLeaves(property.Value, Combine(prefix, property.Key), visit);
            }

            return;
        }

        if (prefix.Length > 0)
        {
            visit(prefix, node);
        }
    }
}
