using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugins.Catalog;

/// <summary>Source-generated JSON metadata for plugin catalogs and source lists.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(CatalogDocument))]
[JsonSerializable(typeof(CatalogSource))]
public partial class CatalogJsonContext : JsonSerializerContext
{
}
