using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugins.Catalog;

/// <summary>
/// A user-configured catalog source. Sources are either HTTPS URLs or local
/// files and folders, so teams can browse a feed without hosting a server.
/// </summary>
public sealed class CatalogSource
{
    /// <summary>Gets or sets the stable source identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTTPS URL or local catalog path.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the source is included when browsing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the source order.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets whether pre-release versions may be shown for this source.</summary>
    public bool AllowPrerelease { get; set; }

    /// <summary>Gets or sets when the source was last refreshed.</summary>
    public DateTimeOffset? LastChecked { get; set; }

    /// <summary>Gets or sets the last refresh error, or <see langword="null"/> after a success.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets whether the source points at a local file or folder.</summary>
    [JsonIgnore]
    public bool IsLocalPath => !IsHttpsUrl(Url);

    /// <summary>Returns whether a value is an absolute HTTPS URL.</summary>
    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>The outcome of adding a catalog source.</summary>
/// <param name="Source">The added source, or the existing source when it already exists.</param>
/// <param name="Error">A clear explanation when the source was rejected.</param>
public sealed record CatalogSourceResult(CatalogSource? Source, string? Error)
{
    /// <summary>Gets a value indicating whether the source was added.</summary>
    public bool Succeeded => Source is not null && Error is null;
}

/// <summary>
/// Persists the user's catalog sources in <c>catalog/sources.json</c>. Adding a
/// source only records a feed URL; it never downloads or installs a plugin.
/// </summary>
public sealed class CatalogSourceStore
{
    /// <summary>The source list file name.</summary>
    public const string FileName = "sources.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private List<CatalogSource> _sources = [];

    private CatalogSourceStore(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Gets the file the sources persist to.</summary>
    public string FilePath { get; }

    /// <summary>Gets the configured sources in priority order.</summary>
    public IReadOnlyList<CatalogSource> Sources
    {
        get
        {
            lock (_sync)
            {
                return _sources.ToArray();
            }
        }
    }

    /// <summary>Loads the sources from a catalog directory; a missing or corrupt file yields no sources.</summary>
    public static async Task<CatalogSourceStore> LoadAsync(
        string catalogDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        var store = new CatalogSourceStore(Path.Combine(catalogDirectory, FileName));
        if (!File.Exists(store.FilePath))
        {
            return store;
        }

        try
        {
            await using var stream = File.OpenRead(store.FilePath);
            var saved = await JsonSerializer
                .DeserializeAsync<SavedSources>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
            store._sources = (saved?.Sources ?? [])
                .Where(source =>
                    !string.IsNullOrWhiteSpace(source.Id) &&
                    !string.IsNullOrWhiteSpace(source.Name) &&
                    !string.IsNullOrWhiteSpace(source.Url))
                .OrderBy(source => source.Priority)
                .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt source list must not prevent the app from starting.
        }

        return store;
    }

    /// <summary>Adds an HTTPS or local source after validating its target.</summary>
    public async Task<CatalogSourceResult> AddAsync(
        string name,
        string url,
        bool allowPrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        var trimmedUrl = url?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0)
        {
            return new CatalogSourceResult(null, "A source name is required.");
        }

        if (trimmedUrl.Length == 0)
        {
            return new CatalogSourceResult(null, "A catalog URL or local path is required.");
        }

        var normalized = NormalizeTarget(trimmedUrl);
        if (normalized.Error is not null)
        {
            return new CatalogSourceResult(null, normalized.Error);
        }

        lock (_sync)
        {
            var existing = _sources.Find(
                source => string.Equals(source.Url, normalized.Url, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return new CatalogSourceResult(
                    existing,
                    $"This source is already added as \"{existing.Name}\".");
            }

            var source = new CatalogSource
            {
                Id = CreateId(normalized.Url!),
                Name = trimmedName,
                Url = normalized.Url!,
                AllowPrerelease = allowPrerelease,
                Priority = _sources.Count == 0 ? 0 : _sources.Max(candidate => candidate.Priority) + 1,
            };
            _sources.Add(source);
            try
            {
                Save();
                return new CatalogSourceResult(source, null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                _sources.Remove(source);
                return new CatalogSourceResult(
                    null,
                    $"The source could not be saved: {exception.Message}");
            }
        }
    }

    /// <summary>Removes a source by id. Cached catalogs and installed plugins are untouched.</summary>
    public Task<bool> RemoveAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return MutateAsync(sourceId, source => true, cancellationToken);
    }

    /// <summary>Enables or disables a source by id.</summary>
    public Task<bool> SetEnabledAsync(
        string sourceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        return MutateAsync(
            sourceId,
            source =>
            {
                source.Enabled = enabled;
                return false;
            },
            cancellationToken);
    }

    /// <summary>Records the outcome of a refresh for a source.</summary>
    public async Task UpdateStatusAsync(
        string sourceId,
        DateTimeOffset? checkedAt,
        string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        lock (_sync)
        {
            var source = _sources.Find(
                candidate => string.Equals(candidate.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return;
            }

            source.LastChecked = checkedAt;
            source.LastError = error;
            try
            {
                Save();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Status is informational; a failed write must not fail a browse.
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Task<bool> MutateAsync(
        string sourceId,
        Func<CatalogSource, bool> mutate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var source = _sources.Find(
                candidate => string.Equals(candidate.Id, sourceId, StringComparison.OrdinalIgnoreCase));
            if (source is null)
            {
                return Task.FromResult(false);
            }

            var remove = mutate(source);
            if (remove)
            {
                _sources.Remove(source);
            }

            try
            {
                Save();
                return Task.FromResult(true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(false);
            }
        }
    }

    private static (string? Url, string? Error) NormalizeTarget(string value)
    {
        if (!Path.IsPathRooted(value) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            !string.IsNullOrEmpty(uri.Scheme))
        {
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                return (uri.AbsoluteUri, null);
            }

            return uri.Scheme == Uri.UriSchemeHttp
                ? (null, "Catalog sources must use HTTPS. Plain HTTP sources are not allowed.")
                : (null, "Only HTTPS URLs and local file or folder paths are supported.");
        }

        try
        {
            var path = Path.GetFullPath(value);
            if (File.Exists(path))
            {
                return (path, null);
            }

            if (Directory.Exists(path))
            {
                return File.Exists(Path.Combine(path, "catalog.json"))
                    ? (path, null)
                    : (null, $"The folder '{path}' does not contain a catalog.json file.");
            }

            return (null, $"The local catalog path '{path}' does not exist.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (null, $"The local catalog path '{value}' is not valid.");
        }
    }

    private static string CreateId(string normalizedUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl.ToLowerInvariant()));
        return "src-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new SavedSources { Sources = _sources }, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    private sealed class SavedSources
    {
        public List<CatalogSource> Sources { get; set; } = [];
    }
}
