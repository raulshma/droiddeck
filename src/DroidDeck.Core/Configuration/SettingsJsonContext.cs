using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Core.Configuration;

/// <summary>Source-generated JSON metadata for settings documents.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public partial class SettingsJsonContext : JsonSerializerContext
{
}
