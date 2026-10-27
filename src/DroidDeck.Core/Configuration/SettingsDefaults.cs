using System.Reflection;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// Loads the embedded built-in defaults document.
/// </summary>
public static class SettingsDefaults
{
    private const string ResourceName = "DroidDeck.Core.Configuration.defaults.json";
    private static readonly Lazy<JsonObject> Defaults = new(LoadDefaults, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Returns a mutable copy of the defaults document.</summary>
    public static JsonObject CreateCopy() => (JsonObject)Defaults.Value.DeepClone();

    /// <summary>Returns the raw defaults JSON.</summary>
    public static string GetJson() => Defaults.Value.ToJsonString(DroidDeckJson.Options);

    private static JsonObject LoadDefaults()
    {
        using var stream = typeof(SettingsDefaults).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonNode.Parse(json, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException("The embedded defaults document is not a JSON object.");
    }
}
