namespace DroidDeck.Core.Toolchain;

/// <summary>The tools the doctor inspects.</summary>
public enum ToolKind
{
    /// <summary>The Android SDK root.</summary>
    AndroidSdk,

    /// <summary>The adb executable.</summary>
    Adb,

    /// <summary>The aapt2 executable.</summary>
    Aapt2,

    /// <summary>The Android emulator.</summary>
    Emulator,

    /// <summary>The Java runtime.</summary>
    Java,

    /// <summary>The Gradle executable.</summary>
    Gradle,

    /// <summary>The Flutter executable.</summary>
    Flutter,

    /// <summary>The Dart executable bundled with Flutter or installed separately.</summary>
    Dart,

    /// <summary>The Node.js executable.</summary>
    Node,

    /// <summary>npm.</summary>
    Npm,

    /// <summary>npx.</summary>
    Npx,

    /// <summary>The scrcpy executable.</summary>
    Scrcpy,

    /// <summary>The Unity editor.</summary>
    Unity,

    /// <summary>The .NET SDK used by MAUI projects.</summary>
    DotNet,

    /// <summary>The .NET MAUI Android workload.</summary>
    MauiAndroidWorkload,
}

/// <summary>Where a tool path came from.</summary>
public enum ToolDetectionSource
{
    /// <summary>The tool was not found.</summary>
    Missing,

    /// <summary>The tool was discovered automatically.</summary>
    Automatic,

    /// <summary>The path was supplied by a user override.</summary>
    Override,
}

/// <summary>The result of checking one tool.</summary>
public sealed record ToolInfo
{
    /// <summary>Gets the tool kind.</summary>
    public required ToolKind Kind { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Gets whether the tool is usable.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Gets the resolved path.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the parsed version.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the detection source.</summary>
    public ToolDetectionSource Source { get; init; }

    /// <summary>Gets fix guidance when the tool is missing or broken.</summary>
    public string? Guidance { get; init; }

    /// <summary>Gets an error raised while probing the tool.</summary>
    public string? Error { get; init; }
}

/// <summary>A cached or freshly detected view of the toolchain.</summary>
public sealed class ToolchainSnapshot
{
    /// <summary>Creates an empty snapshot.</summary>
    public ToolchainSnapshot()
    {
    }

    /// <summary>Creates a snapshot with tools.</summary>
    public ToolchainSnapshot(IReadOnlyList<ToolInfo> tools, DateTimeOffset checkedAt, string? androidSdkPath = null)
    {
        Tools = tools;
        CheckedAt = checkedAt;
        AndroidSdkPath = androidSdkPath;
    }

    /// <summary>Gets all tool results.</summary>
    public IReadOnlyList<ToolInfo> Tools { get; init; } = [];

    /// <summary>Gets when the snapshot was produced.</summary>
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Gets the resolved Android SDK root.</summary>
    public string? AndroidSdkPath { get; init; }

    /// <summary>Gets a tool by kind.</summary>
    public ToolInfo? Get(ToolKind kind) => Tools.FirstOrDefault(tool => tool.Kind == kind);

    /// <summary>Returns whether a tool is available.</summary>
    public bool IsAvailable(ToolKind kind) => Get(kind)?.IsAvailable == true;

    /// <summary>Formats a compact status summary for diagnostics.</summary>
    public string ToSummary()
    {
        if (Tools.Count == 0)
        {
            return "No toolchain snapshot available.";
        }

        return string.Join(
            Environment.NewLine,
            Tools.Select(tool =>
                $"{tool.DisplayName}: {(tool.IsAvailable ? tool.Version ?? "available" : "missing")}" +
                $"{(tool.Path is null ? string.Empty : $" ({tool.Path})")}" +
                $" [{tool.Source}]"));
    }
}

/// <summary>
/// Discovers toolchains and serves a cached snapshot immediately, with a full check on demand.
/// </summary>
public interface IToolchainService
{
    /// <summary>Gets the most recently loaded snapshot, if any.</summary>
    ToolchainSnapshot? CachedSnapshot { get; }

    /// <summary>Loads the snapshot cached on disk, if one exists.</summary>
    Task<ToolchainSnapshot?> LoadCachedAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs a full toolchain check and updates the cache.</summary>
    Task<ToolchainSnapshot> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after a full check completes.</summary>
    event EventHandler<ToolchainSnapshot>? SnapshotChanged;
}
