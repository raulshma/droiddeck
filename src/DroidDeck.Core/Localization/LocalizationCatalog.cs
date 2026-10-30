using System.Text.Json;

namespace DroidDeck.Core.Localization;

/// <summary>
/// The English interface catalog. English is the source language; other
/// languages translate these keys without changing the code that uses them.
/// </summary>
public static class LocalizationCatalog
{
    private const string ResourceName = "DroidDeck.Core.Localization.en-US.json";

    /// <summary>Gets the English strings keyed by localization key.</summary>
    public static IReadOnlyDictionary<string, string> English { get; } = Load();

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(LocalizationCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded catalog '{ResourceName}' is missing.");
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("strings", out var stringsElement) ||
            stringsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"The catalog '{ResourceName}' has no strings object.");
        }

        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in stringsElement.EnumerateObject())
        {
            var value = property.Value.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"The catalog string '{property.Name}' is empty.");
            }

            if (!strings.TryAdd(property.Name, value))
            {
                throw new InvalidOperationException($"The catalog string '{property.Name}' is defined more than once.");
            }
        }

        if (strings.Count == 0)
        {
            throw new InvalidOperationException($"The catalog '{ResourceName}' is empty.");
        }

        return strings;
    }
}
