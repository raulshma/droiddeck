using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.Catalog;

/// <summary>The root document of a plugin catalog.</summary>
public sealed class CatalogDocument
{
    /// <summary>Gets or sets the catalog schema version.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>Gets or sets when the catalog was generated.</summary>
    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>Gets or sets the plugins listed by the catalog.</summary>
    public List<CatalogPlugin> Plugins { get; set; } = [];
}

/// <summary>One plugin listed by a catalog.</summary>
public sealed class CatalogPlugin
{
    /// <summary>Gets or sets the unique plugin identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the publisher name.</summary>
    public string? Publisher { get; set; }

    /// <summary>Gets or sets the one-line summary.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets the longer description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the icon URL.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Gets or sets the project homepage.</summary>
    public string? Homepage { get; set; }

    /// <summary>Gets or sets the license identifier.</summary>
    public string? License { get; set; }

    /// <summary>Gets or sets the tags used for browsing.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Gets or sets the available versions, newest first or not.</summary>
    public List<CatalogVersion> Versions { get; set; } = [];
}

/// <summary>One downloadable version of a catalog plugin.</summary>
public sealed class CatalogVersion
{
    /// <summary>Gets or sets the semantic version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the plugin API version the package targets.</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum host version.</summary>
    public string? MinHostVersion { get; set; }

    /// <summary>Gets or sets the maximum host version.</summary>
    public string? MaxHostVersion { get; set; }

    /// <summary>Gets or sets the package download URL.</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected SHA-256 hash of the package.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional package signature.</summary>
    public string? Signature { get; set; }

    /// <summary>Gets or sets the package size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets when the version was published.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>Gets or sets the version notes.</summary>
    public string? Changelog { get; set; }

    /// <summary>Gets or sets the capabilities the version declares.</summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Gets or sets the plugin dependencies.</summary>
    public List<CatalogDependency> Dependencies { get; set; } = [];
}

/// <summary>A plugin dependency declared by a catalog version.</summary>
public sealed class CatalogDependency
{
    /// <summary>Gets or sets the required plugin identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the minimum required version.</summary>
    public string MinVersion { get; set; } = string.Empty;
}

/// <summary>One validation problem in a catalog document.</summary>
/// <param name="Code">A stable machine-readable issue code.</param>
/// <param name="Message">A human-readable explanation.</param>
public sealed record CatalogValidationIssue(string Code, string Message);

/// <summary>The problems found while validating a catalog document.</summary>
public sealed class CatalogValidationResult
{
    private readonly List<CatalogValidationIssue> _issues = [];

    /// <summary>Gets all issues in the order they were found.</summary>
    public IReadOnlyList<CatalogValidationIssue> Issues => _issues;

    /// <summary>Gets a value indicating whether the catalog can be used.</summary>
    public bool IsValid => _issues.Count == 0;

    /// <summary>Gets every issue message joined into one line.</summary>
    public string Summary => string.Join(" ", _issues.Select(issue => issue.Message));

    internal void Error(string code, string message) => _issues.Add(new CatalogValidationIssue(code, message));
}

/// <summary>The outcome of reading a catalog document.</summary>
/// <param name="Catalog">The catalog, or <see langword="null"/> when it could not be read.</param>
/// <param name="Validation">The issues found while reading the catalog.</param>
public sealed record CatalogReadResult(CatalogDocument? Catalog, CatalogValidationResult Validation)
{
    /// <summary>Gets a value indicating whether the catalog is usable.</summary>
    public bool Succeeded => Catalog is not null && Validation.IsValid;
}

/// <summary>
/// Reads and validates the static JSON catalogs that sources serve. Validation
/// errors are collected so a source author sees every schema problem at once.
/// </summary>
public static partial class PluginCatalogParser
{
    /// <summary>The catalog schema version this host understands.</summary>
    public const int CurrentSchemaVersion = 1;

    private const int MaxNameLength = 128;
    private const int MaxPublisherLength = 128;
    private const int MaxSummaryLength = 512;
    private const int MaxDescriptionLength = 16_384;
    private const int MaxChangelogLength = 16_384;
    private const int MaxTagLength = 64;
    private const int MaxTagCount = 32;

    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    /// <summary>Parses a catalog document from JSON text.</summary>
    public static CatalogReadResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var document = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogDocument);
            return Validate(document);
        }
        catch (JsonException exception)
        {
            return Invalid($"The catalog is not valid JSON: {exception.Message}");
        }
    }

    /// <summary>Reads a catalog document from a stream.</summary>
    public static async Task<CatalogReadResult> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            var document = await JsonSerializer
                .DeserializeAsync(stream, CatalogJsonContext.Default.CatalogDocument, cancellationToken)
                .ConfigureAwait(false);
            return Validate(document);
        }
        catch (JsonException exception)
        {
            return Invalid($"The catalog is not valid JSON: {exception.Message}");
        }
    }

    private static CatalogReadResult Invalid(string message)
    {
        var validation = new CatalogValidationResult();
        validation.Error("catalog.invalidJson", message);
        return new CatalogReadResult(null, validation);
    }

    private static CatalogReadResult Validate(CatalogDocument? document)
    {
        var validation = new CatalogValidationResult();
        if (document is null)
        {
            validation.Error("catalog.empty", "The catalog document was empty.");
            return new CatalogReadResult(null, validation);
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            validation.Error(
                "catalog.schemaVersion.unsupported",
                $"The catalog uses schema version {document.SchemaVersion}, but this host supports version {CurrentSchemaVersion}.");
            return new CatalogReadResult(document, validation);
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = document.Plugins ?? [];
        if (plugins.Count == 0)
        {
            validation.Error("catalog.plugins.required", "The catalog does not list any plugins.");
        }

        foreach (var plugin in plugins)
        {
            ValidatePlugin(plugin, validation, ids);
        }

        return new CatalogReadResult(document, validation);
    }

    private static void ValidatePlugin(
        CatalogPlugin plugin,
        CatalogValidationResult validation,
        HashSet<string> ids)
    {
        var label = string.IsNullOrWhiteSpace(plugin.Name) ? plugin.Id : plugin.Name;
        if (string.IsNullOrWhiteSpace(plugin.Id))
        {
            validation.Error("catalog.plugin.id.required", "A plugin is missing its id.");
        }
        else if (!PluginIdPattern().IsMatch(plugin.Id))
        {
            validation.Error(
                "catalog.plugin.id.invalid",
                $"The plugin id '{plugin.Id}' must be lowercase and contain only letters, digits, dots, and dashes.");
        }
        else if (!ids.Add(plugin.Id))
        {
            validation.Error(
                "catalog.plugin.duplicateId",
                $"The plugin id '{plugin.Id}' is listed more than once.");
        }

        if (string.IsNullOrWhiteSpace(plugin.Name))
        {
            validation.Error("catalog.plugin.name.required", $"The plugin '{label}' is missing its name.");
        }
        else if (plugin.Name.Length > MaxNameLength)
        {
            validation.Error(
                "catalog.plugin.name.tooLong",
                $"The plugin name for '{plugin.Id}' is longer than {MaxNameLength} characters.");
        }

        if (plugin.Publisher?.Length > MaxPublisherLength)
        {
            validation.Error(
                "catalog.plugin.publisher.tooLong",
                $"The publisher for '{label}' is longer than {MaxPublisherLength} characters.");
        }

        if (plugin.Summary?.Length > MaxSummaryLength)
        {
            validation.Error(
                "catalog.plugin.summary.tooLong",
                $"The summary for '{label}' is longer than {MaxSummaryLength} characters.");
        }

        if (plugin.Description?.Length > MaxDescriptionLength)
        {
            validation.Error(
                "catalog.plugin.description.tooLong",
                $"The description for '{label}' is longer than {MaxDescriptionLength} characters.");
        }

        ValidateOptionalUrl(plugin.IconUrl, "iconUrl", label, validation);
        ValidateOptionalUrl(plugin.Homepage, "homepage", label, validation);

        var tags = plugin.Tags ?? [];
        if (tags.Count > MaxTagCount)
        {
            validation.Error(
                "catalog.plugin.tags.tooMany",
                $"The plugin '{label}' declares more than {MaxTagCount} tags.");
        }

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength)
            {
                validation.Error(
                    "catalog.plugin.tag.invalid",
                    $"The plugin '{label}' has an empty or overly long tag.");
                break;
            }
        }

        var versions = plugin.Versions ?? [];
        if (versions.Count == 0)
        {
            validation.Error(
                "catalog.plugin.versions.required",
                $"The plugin '{label}' does not list any versions.");
            return;
        }

        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var version in versions)
        {
            ValidateVersion(version, label, validation, seenVersions);
        }
    }

    private static void ValidateVersion(
        CatalogVersion version,
        string label,
        CatalogValidationResult validation,
        HashSet<string> seenVersions)
    {
        var text = string.IsNullOrWhiteSpace(version.Version) ? "unknown" : version.Version;
        if (!SemanticVersion.TryParse(version.Version, out _))
        {
            validation.Error(
                "catalog.version.invalid",
                $"The version '{text}' of '{label}' is not a semantic version.");
        }
        else if (!seenVersions.Add(version.Version))
        {
            validation.Error(
                "catalog.version.duplicateVersion",
                $"The version '{text}' of '{label}' is listed more than once.");
        }

        if (PluginApi.CheckCompatibility(version.ApiVersion) == ApiCompatibility.Invalid)
        {
            validation.Error(
                "catalog.version.apiVersion.invalid",
                $"The plugin API version '{version.ApiVersion}' of '{label}' {text} is not valid.");
        }

        if (!string.IsNullOrWhiteSpace(version.MinHostVersion) &&
            !SemanticVersion.TryParse(version.MinHostVersion, out _))
        {
            validation.Error(
                "catalog.version.minHostVersion.invalid",
                $"The minimum host version '{version.MinHostVersion}' of '{label}' {text} is not a semantic version.");
        }

        if (!string.IsNullOrWhiteSpace(version.MaxHostVersion) &&
            !SemanticVersion.TryParse(version.MaxHostVersion, out _))
        {
            validation.Error(
                "catalog.version.maxHostVersion.invalid",
                $"The maximum host version '{version.MaxHostVersion}' of '{label}' {text} is not a semantic version.");
        }

        if (SemanticVersion.TryParse(version.MinHostVersion, out var minimum) &&
            SemanticVersion.TryParse(version.MaxHostVersion, out var maximum) &&
            minimum > maximum)
        {
            validation.Error(
                "catalog.version.hostRange.invalid",
                $"The host range of '{label}' {text} ends before it starts.");
        }

        ValidateDownloadUrl(version.DownloadUrl, label, text, validation);

        if (version.Sha256 is null ||
            version.Sha256.Length != 64 ||
            !version.Sha256.All(char.IsAsciiHexDigit))
        {
            validation.Error(
                "catalog.version.sha256.invalid",
                $"The SHA-256 hash of '{label}' {text} must be 64 hexadecimal characters.");
        }

        if (version.Size < 0)
        {
            validation.Error(
                "catalog.version.size.invalid",
                $"The package size of '{label}' {text} cannot be negative.");
        }

        if (version.Changelog?.Length > MaxChangelogLength)
        {
            validation.Error(
                "catalog.version.changelog.tooLong",
                $"The version notes of '{label}' {text} are longer than {MaxChangelogLength} characters.");
        }

        var dependencies = version.Dependencies ?? [];
        var dependencyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.Id) || !PluginIdPattern().IsMatch(dependency.Id))
            {
                validation.Error(
                    "catalog.version.dependency.invalidId",
                    $"The dependency id '{dependency.Id}' of '{label}' {text} is not a valid plugin id.");
            }
            else if (!dependencyIds.Add(dependency.Id))
            {
                validation.Error(
                    "catalog.version.dependency.duplicate",
                    $"The dependency '{dependency.Id}' of '{label}' {text} is listed more than once.");
            }

            if (!SemanticVersion.TryParse(dependency.MinVersion, out _))
            {
                validation.Error(
                    "catalog.version.dependency.invalidVersion",
                    $"The dependency '{dependency.Id}' of '{label}' {text} has an invalid minimum version.");
            }
        }
    }

    private static void ValidateDownloadUrl(
        string value,
        string label,
        string version,
        CatalogValidationResult validation)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            validation.Error(
                "catalog.version.downloadUrl.required",
                $"The '{label}' {version} entry is missing its download URL.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            validation.Error(
                "catalog.version.downloadUrl.invalid",
                $"The download URL '{value}' of '{label}' {version} is not an absolute URL.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            validation.Error(
                "catalog.version.downloadUrl.insecure",
                $"The download URL '{value}' of '{label}' {version} must use HTTPS.");
        }
    }

    private static void ValidateOptionalUrl(
        string? value,
        string field,
        string label,
        CatalogValidationResult validation)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            validation.Error(
                $"catalog.plugin.{field}.invalid",
                $"The {field} of '{label}' is not an absolute HTTP or HTTPS URL.");
        }
    }
}

/// <summary>How a catalog version matches this host.</summary>
public enum CatalogCompatibility
{
    /// <summary>The version matches the host API and host version range.</summary>
    Compatible,

    /// <summary>The version loads with a minor API warning.</summary>
    Partial,

    /// <summary>The version cannot load on this host.</summary>
    Incompatible,
}

/// <summary>A catalog plugin paired with the source that listed it.</summary>
/// <param name="Source">The source the plugin came from.</param>
/// <param name="Plugin">The catalog plugin.</param>
public sealed record CatalogPluginSource(CatalogSource Source, CatalogPlugin Plugin);

/// <summary>Filters applied while browsing catalog entries.</summary>
public sealed record CatalogQuery
{
    /// <summary>Gets the text that must match the plugin metadata, if any.</summary>
    public string? SearchText { get; init; }

    /// <summary>Gets the tags the plugin must declare, matched case-insensitively.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets the source to restrict results to, if any.</summary>
    public string? SourceId { get; init; }

    /// <summary>Gets whether pre-release versions may be selected.</summary>
    public bool IncludePrerelease { get; init; }
}

/// <summary>One plugin version offered by a catalog and matched to this host.</summary>
public sealed record CatalogEntry
{
    /// <summary>Gets the source that listed the plugin.</summary>
    public required CatalogSource Source { get; init; }

    /// <summary>Gets the catalog plugin.</summary>
    public required CatalogPlugin Plugin { get; init; }

    /// <summary>Gets the version selected for this host.</summary>
    public required CatalogVersion Version { get; init; }

    /// <summary>Gets the compatibility of the selected version.</summary>
    public required CatalogCompatibility Compatibility { get; init; }

    /// <summary>Gets the explanation shown next to the compatibility badge.</summary>
    public required string CompatibilityLabel { get; init; }

    /// <summary>Gets whether the selected version is a pre-release.</summary>
    public required bool IsPrerelease { get; init; }

    /// <summary>Gets the version notes, when the catalog has them.</summary>
    public string? Changelog => Version.Changelog;
}

/// <summary>
/// Selects the best version for this host and applies catalog search filters.
/// Nothing here downloads or installs a package.
/// </summary>
public static class CatalogBrowser
{
    /// <summary>Browses catalog plugins for the given host and query.</summary>
    public static IReadOnlyList<CatalogEntry> Browse(
        IEnumerable<CatalogPluginSource> plugins,
        CatalogQuery? query,
        string hostVersion,
        string hostApiVersion = PluginApi.Version)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        query ??= new CatalogQuery();
        var terms = (query.SearchText ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var entries = new List<CatalogEntry>();
        foreach (var pair in plugins)
        {
            if (query.SourceId is not null &&
                !string.Equals(pair.Source.Id, query.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!HasTags(pair.Plugin, query.Tags) || !MatchesText(pair.Plugin, terms))
            {
                continue;
            }

            var selected = SelectVersion(pair, query, hostVersion, hostApiVersion, out var compatibility, out var label);
            if (selected is null)
            {
                continue;
            }

            entries.Add(new CatalogEntry
            {
                Source = pair.Source,
                Plugin = pair.Plugin,
                Version = selected,
                Compatibility = compatibility,
                CompatibilityLabel = label,
                IsPrerelease = SemanticVersion.TryParse(selected.Version, out var parsed) && parsed.PreRelease is not null,
            });
        }

        return entries
            .OrderBy(entry => entry.Compatibility)
            .ThenBy(entry => entry.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Plugin.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasTags(CatalogPlugin plugin, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return true;
        }

        var declared = plugin.Tags ?? [];
        foreach (var tag in tags)
        {
            if (!declared.Exists(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesText(CatalogPlugin plugin, string[] terms)
    {
        if (terms.Length == 0)
        {
            return true;
        }

        var haystack = string.Join(
            ' ',
            new[]
            {
                plugin.Id,
                plugin.Name,
                plugin.Publisher,
                plugin.Summary,
                plugin.Description,
            }.Concat(plugin.Tags ?? []));
        foreach (var term in terms)
        {
            if (!haystack.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static CatalogVersion? SelectVersion(
        CatalogPluginSource pair,
        CatalogQuery query,
        string hostVersion,
        string hostApiVersion,
        out CatalogCompatibility compatibility,
        out string label)
    {
        compatibility = CatalogCompatibility.Incompatible;
        label = string.Empty;
        var includePrerelease = query.IncludePrerelease && pair.Source.AllowPrerelease;
        var candidates = new List<(CatalogVersion Version, SemanticVersion Parsed)>();
        foreach (var version in pair.Plugin.Versions ?? [])
        {
            if (!SemanticVersion.TryParse(version.Version, out var parsed) ||
                (!includePrerelease && parsed.PreRelease is not null))
            {
                continue;
            }

            candidates.Add((version, parsed));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var evaluated = candidates
            .Select(candidate => (
                candidate.Version,
                candidate.Parsed,
                Result: Evaluate(candidate.Version, hostVersion, hostApiVersion)))
            .ToArray();
        var compatible = evaluated
            .Where(candidate => candidate.Result.Kind != CatalogCompatibility.Incompatible)
            .OrderByDescending(candidate => candidate.Parsed)
            .FirstOrDefault();
        var selected = compatible.Version is not null
            ? compatible
            : evaluated.OrderByDescending(candidate => candidate.Parsed).First();
        compatibility = selected.Result.Kind;
        label = selected.Result.Label;
        return selected.Version;
    }

    private static (CatalogCompatibility Kind, string Label) Evaluate(
        CatalogVersion version,
        string hostVersion,
        string hostApiVersion)
    {
        var api = PluginApi.CheckCompatibility(version.ApiVersion, hostApiVersion);
        if (api == ApiCompatibility.Invalid)
        {
            return (CatalogCompatibility.Incompatible,
                $"The plugin API version '{version.ApiVersion}' is not valid.");
        }

        if (api == ApiCompatibility.MajorMismatch)
        {
            return (CatalogCompatibility.Incompatible,
                $"Targets plugin API {version.ApiVersion}; this host implements {hostApiVersion}.");
        }

        if (SemanticVersion.TryParse(hostVersion, out var host))
        {
            if (SemanticVersion.TryParse(version.MinHostVersion, out var minimum) && host < minimum)
            {
                return (CatalogCompatibility.Incompatible,
                    $"Requires DroidDeck {minimum} or newer; this is {host}.");
            }

            if (SemanticVersion.TryParse(version.MaxHostVersion, out var maximum) && host > maximum)
            {
                return (CatalogCompatibility.Incompatible,
                    $"Supports DroidDeck up to {maximum}; this is {host}.");
            }
        }

        if (api == ApiCompatibility.MinorMismatch)
        {
            return (CatalogCompatibility.Partial,
                $"Targets plugin API {version.ApiVersion}; this host implements {hostApiVersion}. It loads with a compatibility warning.");
        }

        return (CatalogCompatibility.Compatible, $"Compatible with DroidDeck {hostVersion}.");
    }
}
