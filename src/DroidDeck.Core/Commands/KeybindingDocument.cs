using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Core.Commands;

/// <summary>The persisted keybindings document.</summary>
internal sealed record KeybindingDocument
{
    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets command identifier to canonical shortcut text overrides.</summary>
    public Dictionary<string, string> Overrides { get; init; } = new();
}

/// <summary>Serialization options for the keybindings document; source-generated metadata with
/// relaxed escaping so users can hand-edit shortcuts like <c>Ctrl+Alt+R</c>.</summary>
internal static class KeybindingsJson
{
    /// <summary>Gets the shared serializer options.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = KeybindingsJsonContext.Default,
    };
}

/// <summary>Source-generated JSON metadata for the keybindings document.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(KeybindingDocument))]
internal sealed partial class KeybindingsJsonContext : JsonSerializerContext
{
}
