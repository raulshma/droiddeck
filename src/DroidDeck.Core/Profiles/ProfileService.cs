using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Infrastructure;

namespace DroidDeck.Core.Profiles;

/// <summary>
/// Manages named settings profiles stored as a metadata index plus one settings
/// layer per profile. Profile layers participate in the layered configuration
/// source map; exports never include secret values.
/// </summary>
public sealed class ProfileService : IDisposable
{
    private const string IndexFileName = "index.json";
    private readonly AppPaths _paths;
    private readonly IConfigurationService _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a profile service over the application data paths.</summary>
    public ProfileService(
        AppPaths paths,
        IConfigurationService configuration,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the directory that holds profile layers and the index.</summary>
    public string ProfilesDirectory => _paths.ProfilesDirectory;

    /// <summary>Gets the profile index file.</summary>
    public string IndexFile => Path.Combine(_paths.ProfilesDirectory, IndexFileName);

    /// <summary>Lists known profiles ordered by name.</summary>
    public async Task<IReadOnlyList<ProfileMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            return index.Profiles
                .OrderBy(static profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(static profile => profile.Id, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets one profile and its settings layer.</summary>
    public async Task<ProfileDocument?> GetAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ValidateId(profileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var metadata = index.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (metadata is null)
            {
                return null;
            }

            return new ProfileDocument(metadata, await LoadLayerLockedAsync(metadata.Id, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Creates a profile and optionally seeds its settings layer.</summary>
    public async Task<ProfileMetadata> CreateAsync(
        string name,
        JsonObject? settings = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var id = CreateUniqueId(index, name);
            var metadata = new ProfileMetadata
            {
                Id = id,
                Name = name.Trim(),
                Description = description?.Trim() ?? string.Empty,
                CreatedAt = now,
                UpdatedAt = now,
            };

            index.Profiles.Add(metadata);
            await SaveLayerLockedAsync(id, settings ?? new JsonObject(), cancellationToken).ConfigureAwait(false);
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Replaces a profile's settings layer.</summary>
    public async Task<ProfileMetadata> SaveAsync(
        string profileId,
        JsonObject settings,
        CancellationToken cancellationToken = default)
    {
        ValidateId(profileId);
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var metadata = FindRequired(index, profileId);
            await SaveLayerLockedAsync(metadata.Id, settings, cancellationToken).ConfigureAwait(false);
            metadata.UpdatedAt = _timeProvider.GetUtcNow();
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Renames a profile and optionally updates its description.</summary>
    public async Task<ProfileMetadata> UpdateMetadataAsync(
        string profileId,
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(profileId);
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var metadata = FindRequired(index, profileId);
            metadata.Name = name.Trim();
            if (description is not null)
            {
                metadata.Description = description.Trim();
            }

            metadata.UpdatedAt = _timeProvider.GetUtcNow();
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
            return metadata;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Copies a profile and its settings under a new name.</summary>
    public async Task<ProfileMetadata> DuplicateAsync(
        string profileId,
        string? newName = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(profileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var source = FindRequired(index, profileId);
            var settings = await LoadLayerLockedAsync(source.Id, cancellationToken).ConfigureAwait(false);
            var name = string.IsNullOrWhiteSpace(newName) ? source.Name + " copy" : newName;
            ValidateName(name);
            var now = _timeProvider.GetUtcNow();
            var copy = new ProfileMetadata
            {
                Id = CreateUniqueId(index, name),
                Name = name.Trim(),
                Description = source.Description,
                CreatedAt = now,
                UpdatedAt = now,
            };

            index.Profiles.Add(copy);
            await SaveLayerLockedAsync(copy.Id, settings, cancellationToken).ConfigureAwait(false);
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
            return copy;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes a profile and its layer. The active profile is deactivated first.</summary>
    public async Task<bool> DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ValidateId(profileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var metadata = index.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (metadata is null)
            {
                return false;
            }

            if (string.Equals(index.ActiveProfileId, metadata.Id, StringComparison.OrdinalIgnoreCase))
            {
                index.ActiveProfileId = null;
                await _configuration.UseProfileAsync(null, cancellationToken).ConfigureAwait(false);
            }

            index.Profiles.Remove(metadata);
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
            var file = ProfileFile(metadata.Id);
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies a profile, or deactivates the active profile with <see langword="null"/>.</summary>
    public async Task ApplyAsync(string? profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            if (profileId is not null)
            {
                FindRequired(index, profileId);
            }

            await _configuration.UseProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            index.ActiveProfileId = profileId;
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets the profile currently applied to configuration.</summary>
    public string? GetActiveProfileId() =>
        _configuration.GetString(SettingsPaths.ActiveProfileId);

    /// <summary>Exports one profile as a portable, secret-free JSON document.</summary>
    public async Task<string> ExportAsync(string profileId, CancellationToken cancellationToken = default)
    {
        ValidateId(profileId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var metadata = FindRequired(index, profileId);
            var settings = await LoadLayerLockedAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
            var excluded = new List<string>();
            var sanitized = SettingsTransfer.Sanitize(settings, excluded);
            var document = new JsonObject
            {
                ["formatVersion"] = 1,
                ["exportedAt"] = _timeProvider.GetUtcNow(),
                ["profile"] = new JsonObject
                {
                    ["id"] = metadata.Id,
                    ["name"] = metadata.Name,
                    ["description"] = metadata.Description,
                    ["createdAt"] = metadata.CreatedAt,
                    ["updatedAt"] = metadata.UpdatedAt,
                },
                ["settings"] = sanitized,
                ["excludedPaths"] = new JsonArray(excluded.Select(static path => (JsonNode?)path).ToArray()),
            };
            return document.ToJsonString(DroidDeckJson.Options);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Imports a profile exported by <see cref="ExportAsync"/> or a bare settings
    /// document. Secret paths are removed before anything is written.
    /// </summary>
    public async Task<ProfileImportResult> ImportAsync(
        string json,
        ProfileImportMode mode = ProfileImportMode.Copy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
                ?? throw new ProfileException("The profile document must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new ProfileException("The profile document is not valid JSON: " + exception.Message);
        }

        var profileNode = root["profile"] as JsonObject;
        var name = profileNode?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Imported profile";
        }

        var description = profileNode?["description"]?.GetValue<string>() ?? string.Empty;
        var requestedId = profileNode?["id"]?.GetValue<string>();
        // A "settings" member carries the layer; without one the whole document
        // is treated as a bare settings object (a hand-written profile file).
        var settings = root["settings"] switch
        {
            null => root,
            JsonObject document => document,
            _ => throw new ProfileException("The profile settings must be a JSON object."),
        };

        var excluded = new List<string>();
        if (root["excludedPaths"] is JsonArray previouslyExcluded)
        {
            excluded.AddRange(previouslyExcluded
                .Select(static node => node?.GetValue<string>())
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!));
        }

        var sanitized = SettingsTransfer.Sanitize(settings, excluded);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexLockedAsync(cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var existing = mode == ProfileImportMode.Replace && !string.IsNullOrWhiteSpace(requestedId)
                ? index.Profiles.FirstOrDefault(profile =>
                    string.Equals(profile.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                : null;

            ProfileMetadata metadata;
            if (existing is not null)
            {
                metadata = existing;
                metadata.Name = name.Trim();
                metadata.Description = description.Trim();
                metadata.UpdatedAt = now;
            }
            else
            {
                metadata = new ProfileMetadata
                {
                    Id = CreateUniqueId(index, name),
                    Name = name.Trim(),
                    Description = description.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                index.Profiles.Add(metadata);
            }

            await SaveLayerLockedAsync(metadata.Id, sanitized, cancellationToken).ConfigureAwait(false);
            await SaveIndexLockedAsync(index, cancellationToken).ConfigureAwait(false);
            return new ProfileImportResult(metadata, excluded, existing is not null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ProfileFile(string profileId) =>
        Path.Combine(_paths.ProfilesDirectory, profileId + ".json");

    private async Task<ProfileIndexDocument> LoadIndexLockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(IndexFile))
        {
            return new ProfileIndexDocument();
        }

        try
        {
            var text = await File.ReadAllTextAsync(IndexFile, cancellationToken).ConfigureAwait(false);
            var index = DroidDeckJson.Deserialize<ProfileIndexDocument>(text);
            if (index is null)
            {
                return new ProfileIndexDocument();
            }

            index.Profiles = index.Profiles
                .Where(static profile => !string.IsNullOrWhiteSpace(profile.Id))
                .GroupBy(static profile => profile.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();
            return index;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ProfileIndexDocument();
        }
    }

    private async Task<JsonObject> LoadLayerLockedAsync(string profileId, CancellationToken cancellationToken)
    {
        var file = ProfileFile(profileId);
        if (!File.Exists(file))
        {
            return new JsonObject();
        }

        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(text, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
                ?? new JsonObject();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject();
        }
    }

    private Task SaveIndexLockedAsync(ProfileIndexDocument index, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(
            IndexFile,
            DroidDeckJson.Serialize(index),
            cancellationToken: cancellationToken);

    private Task SaveLayerLockedAsync(string profileId, JsonObject settings, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(
            ProfileFile(profileId),
            settings.ToJsonString(DroidDeckJson.Options),
            cancellationToken: cancellationToken);

    private static ProfileMetadata FindRequired(ProfileIndexDocument index, string profileId) =>
        index.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ProfileException($"The profile '{profileId}' does not exist.");

    private static string CreateUniqueId(ProfileIndexDocument index, string name)
    {
        var slug = Slug(name);
        var candidate = slug;
        var suffix = 2;
        while (index.Profiles.Any(profile =>
            string.Equals(profile.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }

    private static string Slug(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "profile";
        }

        return slug.Length > 48 ? slug[..48].TrimEnd('-') : slug;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > 80)
        {
            throw new ProfileException("A profile name must be 80 characters or fewer.");
        }
    }

    private static void ValidateId(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!profileId.All(static character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ProfileException($"'{profileId}' is not a valid profile identifier.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
