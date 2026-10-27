using System.Text.Json.Serialization;

namespace DroidDeck.Core.Toolchain;

internal sealed class ToolchainCacheDocument
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset CheckedAt { get; set; }

    public string? AndroidSdkPath { get; set; }

    public List<ToolchainCacheTool> Tools { get; set; } = [];

    public static ToolchainCacheDocument FromSnapshot(ToolchainSnapshot snapshot) => new()
    {
        CheckedAt = snapshot.CheckedAt,
        AndroidSdkPath = snapshot.AndroidSdkPath,
        Tools = snapshot.Tools
            .Select(tool => new ToolchainCacheTool
            {
                Kind = tool.Kind,
                DisplayName = tool.DisplayName,
                IsAvailable = tool.IsAvailable,
                Path = tool.Path,
                Version = tool.Version,
                Source = tool.Source,
                Guidance = tool.Guidance,
                Error = tool.Error,
            })
            .ToList(),
    };

    public ToolchainSnapshot ToSnapshot() => new(
        Tools
            .Select(tool => new ToolInfo
            {
                Kind = tool.Kind,
                DisplayName = tool.DisplayName,
                IsAvailable = tool.IsAvailable,
                Path = tool.Path,
                Version = tool.Version,
                Source = tool.Source,
                Guidance = tool.Guidance,
                Error = tool.Error,
            })
            .ToList(),
        CheckedAt,
        AndroidSdkPath);
}

internal sealed class ToolchainCacheTool
{
    public ToolKind Kind { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public bool IsAvailable { get; set; }

    public string? Path { get; set; }

    public string? Version { get; set; }

    public ToolDetectionSource Source { get; set; }

    public string? Guidance { get; set; }

    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ToolchainCacheDocument))]
internal sealed partial class ToolchainJsonContext : JsonSerializerContext
{
}
