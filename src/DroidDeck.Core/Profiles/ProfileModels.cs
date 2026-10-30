using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DroidDeck.Core.Profiles;

/// <summary>Metadata for a named settings profile.</summary>
public sealed class ProfileMetadata
{
    /// <summary>Gets or sets the stable profile identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional explanation shown in the profile list.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets when the profile was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets when the profile was last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets whether the profile is supplied by the application.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>A profile with its settings layer.</summary>
/// <param name="Metadata">The profile metadata.</param>
/// <param name="Settings">The settings layer, without secrets.</param>
public sealed record ProfileDocument(ProfileMetadata Metadata, JsonObject Settings);

/// <summary>The persisted profile index.</summary>
internal sealed class ProfileIndexDocument
{
    /// <summary>Gets or sets the index schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Gets or sets the profile the configuration service last applied.</summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>Gets or sets the known profiles.</summary>
    public List<ProfileMetadata> Profiles { get; set; } = [];
}

/// <summary>What to do when an imported profile identifier already exists.</summary>
public enum ProfileImportMode
{
    /// <summary>Create a copy with a new identifier.</summary>
    Copy,

    /// <summary>Replace the settings of the existing profile.</summary>
    Replace,
}

/// <summary>The result of importing a profile.</summary>
/// <param name="Metadata">The imported profile metadata.</param>
/// <param name="ExcludedPaths">Sensitive paths removed from the imported settings.</param>
/// <param name="Replaced">Whether an existing profile was replaced.</param>
public sealed record ProfileImportResult(
    ProfileMetadata Metadata,
    IReadOnlyList<string> ExcludedPaths,
    bool Replaced);

/// <summary>Raised when a profile document cannot be accepted.</summary>
public sealed class ProfileException(string message) : Exception(message);
