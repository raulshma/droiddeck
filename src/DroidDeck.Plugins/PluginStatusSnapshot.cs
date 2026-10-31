using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// The identity, issues, and trust evidence of one installed plugin. Every
/// manager status is produced from one snapshot so a branch cannot silently
/// omit the trust decision or a hash.
/// </summary>
public sealed record PluginStatusSnapshot
{
    /// <summary>Gets the plugin identifier, or the directory name when the manifest is unreadable.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the display name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the plugin version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the target API version.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>Gets the declared publisher, when one exists.</summary>
    public string? Author { get; init; }

    /// <summary>Gets the capability names declared by the manifest.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Gets the plugin installation directory.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Gets the validation and load issues found for the plugin.</summary>
    public IReadOnlyList<PluginValidationIssue> Issues { get; init; } = [];

    /// <summary>Gets the trust decision for the plugin contents.</summary>
    public PluginTrustDecision Trust { get; init; } = PluginTrustDecision.Trusted;

    /// <summary>Gets the hash of the current plugin contents, when it was computed.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Gets the hash that was approved for the plugin, when one exists.</summary>
    public string? ApprovedHash { get; init; }

    /// <summary>Creates the snapshot for a readable manifest.</summary>
    /// <param name="manifest">The manifest that was read.</param>
    /// <param name="installPath">The plugin installation directory.</param>
    /// <param name="issues">The validation issues found for the plugin.</param>
    public static PluginStatusSnapshot FromManifest(
        PluginManifest manifest,
        string installPath,
        IReadOnlyList<PluginValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(issues);
        return new PluginStatusSnapshot
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            ApiVersion = manifest.ApiVersion,
            Author = manifest.Author,
            Capabilities = manifest.Capabilities,
            InstallPath = installPath,
            Issues = issues,
        };
    }

    /// <summary>Creates the placeholder snapshot for a directory whose manifest could not be read.</summary>
    /// <param name="installPath">The plugin installation directory.</param>
    /// <param name="issues">The manifest read issues.</param>
    public static PluginStatusSnapshot ForUnreadableManifest(
        string installPath,
        IReadOnlyList<PluginValidationIssue> issues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(issues);
        return new PluginStatusSnapshot
        {
            Id = Path.GetFileName(installPath),
            Name = "Unknown plugin",
            Version = string.Empty,
            ApiVersion = string.Empty,
            InstallPath = installPath,
            Issues = issues,
        };
    }

    /// <summary>Creates the manager status for this snapshot.</summary>
    /// <param name="state">The lifecycle state.</param>
    /// <param name="reason">The human-readable reason, when one exists.</param>
    /// <param name="loadDuration">How long loading took; zero for plugins that did not load.</param>
    public PluginStatus ToStatus(PluginState state, string? reason, TimeSpan loadDuration = default)
        => new()
        {
            Id = Id,
            Name = Name,
            Version = Version,
            ApiVersion = ApiVersion,
            Author = Author,
            Capabilities = Capabilities,
            InstallPath = InstallPath,
            State = state,
            Trust = Trust,
            ContentHash = ContentHash,
            ApprovedHash = ApprovedHash,
            Reason = reason,
            Issues = Issues,
            LoadDuration = loadDuration,
        };
}
