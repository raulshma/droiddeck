using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// Source-generated JSON metadata for plugin manifests.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PluginManifest))]
public partial class PluginJsonContext : JsonSerializerContext
{
}
