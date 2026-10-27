using System.Text.Json.Serialization;

namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// A dependency on another plugin.
/// </summary>
public sealed class PluginDependency
{
    /// <summary>Gets or sets the required plugin identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum required version.</summary>
    public string MinVersion { get; set; } = string.Empty;
}

/// <summary>
/// The manifest stored next to a plugin assembly.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>Gets or sets the unique plugin identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the publisher name.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets the description shown in the plugin manager.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the target plugin API version.</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum host version.</summary>
    public string? MinHostVersion { get; set; }

    /// <summary>Gets or sets the maximum host version, if any.</summary>
    public string? MaxHostVersion { get; set; }

    /// <summary>Gets or sets the entry assembly file name.</summary>
    public string EntryAssembly { get; set; } = string.Empty;

    /// <summary>Gets or sets the fully qualified entry type name.</summary>
    public string EntryType { get; set; } = string.Empty;

    /// <summary>Gets or sets the declared capability names.</summary>
    public IReadOnlyList<string> Capabilities { get; set; } = [];

    /// <summary>Gets or sets the plugin dependencies.</summary>
    public IReadOnlyList<PluginDependency> Dependencies { get; set; } = [];

    /// <summary>Gets or sets the icon file name.</summary>
    public string? Icon { get; set; }

    /// <summary>Gets or sets the project homepage.</summary>
    public string? Homepage { get; set; }

    /// <summary>Gets or sets the license identifier.</summary>
    public string? License { get; set; }

    /// <summary>Gets the recognized capabilities declared by the manifest.</summary>
    [JsonIgnore]
    public PluginCapability KnownCapabilities => PluginCapabilities.Parse(Capabilities).Known;

    /// <summary>Gets unknown capability names declared by the manifest.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> UnknownCapabilities => PluginCapabilities.Parse(Capabilities).Unknown;

    /// <summary>Returns whether the manifest declares a capability.</summary>
    public bool HasCapability(PluginCapability capability) => (KnownCapabilities & capability) == capability;
}
