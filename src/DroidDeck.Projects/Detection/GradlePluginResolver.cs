using System.Text.RegularExpressions;

namespace DroidDeck.Projects.Detection;

/// <summary>One plugin a Gradle build script declares.</summary>
/// <param name="Id">The plugin id.</param>
/// <param name="Version">The version resolved from the version catalog or the build script, when known.</param>
/// <param name="IsApplied">Whether the plugin is applied rather than declared with <c>apply false</c>.</param>
internal sealed record GradlePluginUse(string Id, string? Version, bool IsApplied);

/// <summary>
/// Resolves the plugins a Gradle build script applies. Version-catalog aliases such as
/// <c>alias(libs.plugins.android.application)</c> map to catalog plugin ids, and local convention
/// plugins from included builds or <c>buildSrc</c> expand to the plugins their sources apply.
/// </summary>
internal sealed partial class GradlePluginResolver
{
    /// <summary>The Android application plugin id.</summary>
    internal const string AndroidApplicationPlugin = "com.android.application";

    /// <summary>The Android library plugin id.</summary>
    internal const string AndroidLibraryPlugin = "com.android.library";

    /// <summary>The AGP Kotlin Multiplatform library plugin id.</summary>
    internal const string AndroidKmpLibraryPlugin = "com.android.kotlin.multiplatform.library";

    /// <summary>The Kotlin Multiplatform plugin id.</summary>
    internal const string KotlinMultiplatformPlugin = "org.jetbrains.kotlin.multiplatform";

    /// <summary>The Compose Multiplatform plugin id.</summary>
    internal const string ComposeMultiplatformPlugin = "org.jetbrains.compose";

    private readonly Dictionary<string, CatalogPlugin> _catalogPlugins;
    private readonly Dictionary<string, string> _pluginVersions;
    private readonly Dictionary<string, IReadOnlyCollection<string>> _conventions;

    private GradlePluginResolver(
        Dictionary<string, CatalogPlugin> catalogPlugins,
        Dictionary<string, string> pluginVersions,
        Dictionary<string, IReadOnlyCollection<string>> conventions)
    {
        _catalogPlugins = catalogPlugins;
        _pluginVersions = pluginVersions;
        _conventions = conventions;
    }

    /// <summary>Gets the Android Gradle Plugin version declared by the version catalog.</summary>
    internal string? AgpVersion => FirstVersion(
        AndroidApplicationPlugin,
        AndroidLibraryPlugin,
        AndroidKmpLibraryPlugin,
        "com.android.test");

    /// <summary>Gets the Kotlin plugin version declared by the version catalog.</summary>
    internal string? KotlinVersion
    {
        get
        {
            var known = FirstVersion(
                KotlinMultiplatformPlugin,
                "org.jetbrains.kotlin.android",
                "org.jetbrains.kotlin.jvm",
                "org.jetbrains.kotlin.plugin.compose");
            return known ?? _pluginVersions
                .Where(pair => pair.Key.StartsWith("org.jetbrains.kotlin.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Value)
                .FirstOrDefault();
        }
    }

    /// <summary>Loads the version catalog and local convention plugins for a project root.</summary>
    /// <param name="root">The absolute project root.</param>
    /// <param name="settingsText">The text of the settings script, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token that cancels file reads.</param>
    /// <returns>A resolver for the project.</returns>
    internal static async Task<GradlePluginResolver> LoadAsync(
        string root,
        string? settingsText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var (catalogPlugins, pluginVersions) = await LoadCatalogAsync(root, cancellationToken).ConfigureAwait(false);
        var conventions = await LoadConventionsAsync(root, settingsText, cancellationToken).ConfigureAwait(false);
        return new GradlePluginResolver(catalogPlugins, pluginVersions, conventions);
    }

    /// <summary>Gets the catalog version for a plugin id, when one is declared.</summary>
    /// <param name="pluginId">The plugin id.</param>
    /// <returns>The catalog version, or <see langword="null"/>.</returns>
    internal string? GetVersion(string pluginId) =>
        _pluginVersions.TryGetValue(pluginId, out var version) ? version : null;

    /// <summary>Parses the plugin declarations in a build script.</summary>
    /// <param name="buildText">The build script text, or <see langword="null"/>.</param>
    /// <returns>The declared plugins, merged by id.</returns>
    internal IReadOnlyList<GradlePluginUse> ParseUses(string? buildText)
    {
        if (string.IsNullOrWhiteSpace(buildText))
        {
            return [];
        }

        var text = ProjectSource.StripLineComments(buildText);
        var uses = new Dictionary<string, GradlePluginUse>(StringComparer.OrdinalIgnoreCase);
        var pluginsBlock = ProjectSource.ExtractBlockBody(text, "plugins");
        if (pluginsBlock is not null)
        {
            foreach (var rawLine in pluginsBlock.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var applied = !ApplyFalseRegex().IsMatch(line);
                AddUse(uses, ParsePluginLine(line, applied));
            }
        }

        foreach (Match match in AppliedPluginRegex().Matches(text))
        {
            var id = match.Groups["id"].Value;
            AddUse(uses, new GradlePluginUse(id, GetVersion(id), IsApplied: true));
        }

        foreach (Match match in AppliedPluginGroovyRegex().Matches(text))
        {
            var id = match.Groups["id"].Value;
            AddUse(uses, new GradlePluginUse(id, GetVersion(id), IsApplied: true));
        }

        return uses.Values.ToList();
    }

    /// <summary>Expands the applied plugins through local convention plugin definitions.</summary>
    /// <param name="uses">The declared plugins.</param>
    /// <returns>Every applied plugin id, including those applied by convention plugins.</returns>
    internal IReadOnlySet<string> Expand(IEnumerable<GradlePluginUse> uses)
    {
        ArgumentNullException.ThrowIfNull(uses);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var use in uses.Where(use => use.IsApplied))
        {
            ids.Add(use.Id);
            if (_conventions.TryGetValue(use.Id, out var effective))
            {
                ids.UnionWith(effective);
            }
        }

        return ids;
    }

    private GradlePluginUse? ParsePluginLine(string line, bool applied)
    {
        var alias = PluginAliasRegex().Match(line);
        if (!alias.Success)
        {
            alias = PluginAliasBracketRegex().Match(line);
        }

        if (alias.Success)
        {
            var accessor = NormalizeAccessor(alias.Groups["path"].Value);
            return _catalogPlugins.TryGetValue(accessor, out var catalogPlugin)
                ? new GradlePluginUse(
                    catalogPlugin.Id,
                    catalogPlugin.Version ?? GetVersion(catalogPlugin.Id),
                    applied)
                : null;
        }

        var idMatch = PluginIdFunctionRegex().Match(line);
        if (!idMatch.Success)
        {
            idMatch = PluginIdGroovyRegex().Match(line);
        }

        if (idMatch.Success)
        {
            var id = idMatch.Groups["id"].Value;
            return new GradlePluginUse(id, VersionLiteral(line) ?? GetVersion(id), applied);
        }

        var kotlinMatch = KotlinFunctionRegex().Match(line);
        if (kotlinMatch.Success)
        {
            var id = "org.jetbrains.kotlin." + kotlinMatch.Groups["name"].Value;
            return new GradlePluginUse(id, VersionLiteral(line) ?? GetVersion(id), applied);
        }

        return null;
    }

    private static string? VersionLiteral(string line)
    {
        var match = VersionLiteralRegex().Match(line);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static void AddUse(Dictionary<string, GradlePluginUse> uses, GradlePluginUse? use)
    {
        if (use is null)
        {
            return;
        }

        if (uses.TryGetValue(use.Id, out var existing))
        {
            uses[use.Id] = existing with
            {
                IsApplied = existing.IsApplied || use.IsApplied,
                Version = existing.Version ?? use.Version,
            };
            return;
        }

        uses[use.Id] = use;
    }

    private static string NormalizeAccessor(string value) =>
        value.Replace('-', '.').Replace('_', '.').ToLowerInvariant();

    private string? FirstVersion(params string[] pluginIds)
    {
        foreach (var id in pluginIds)
        {
            if (_pluginVersions.TryGetValue(id, out var version))
            {
                return version;
            }
        }

        return null;
    }

    private static async Task<(Dictionary<string, CatalogPlugin> Plugins, Dictionary<string, string> Versions)> LoadCatalogAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var plugins = new Dictionary<string, CatalogPlugin>(StringComparer.OrdinalIgnoreCase);
        var pluginVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var catalogPath = Path.Combine(root, "gradle", "libs.versions.toml");
        var catalogText = await ProjectSource.ReadTextAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(catalogText))
        {
            return (plugins, pluginVersions);
        }

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new List<(string Alias, string Id, string? Version, string? VersionRef)>();
        var section = string.Empty;
        foreach (var rawLine in catalogText.Split('\n'))
        {
            var line = ProjectSource.StripLineComments(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim().Trim('"', '\'');
            var value = line[(equals + 1)..].Trim();
            if (section.Equals("versions", StringComparison.OrdinalIgnoreCase))
            {
                versions[key] = value.Trim().Trim(',').Trim('"', '\'');
                continue;
            }

            if (!section.Equals("plugins", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var idMatch = CatalogPluginIdRegex().Match(value);
            if (!idMatch.Success)
            {
                continue;
            }

            var versionRef = CatalogVersionRefRegex().Match(value);
            var versionLiteral = CatalogVersionLiteralRegex().Match(value);
            declarations.Add((
                key,
                idMatch.Groups["id"].Value,
                versionLiteral.Success ? versionLiteral.Groups["version"].Value : null,
                versionRef.Success ? versionRef.Groups["ref"].Value : null));
        }

        foreach (var declaration in declarations)
        {
            var version = declaration.Version;
            if (version is null &&
                declaration.VersionRef is not null &&
                versions.TryGetValue(declaration.VersionRef, out var resolved))
            {
                version = resolved;
            }

            plugins[NormalizeAccessor(declaration.Alias)] = new CatalogPlugin(declaration.Id, version);
            if (version is not null && !pluginVersions.ContainsKey(declaration.Id))
            {
                pluginVersions[declaration.Id] = version;
            }
        }

        return (plugins, pluginVersions);
    }

    private static async Task<Dictionary<string, IReadOnlyCollection<string>>> LoadConventionsAsync(
        string root,
        string? settingsText,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        if (!string.IsNullOrWhiteSpace(settingsText))
        {
            var text = ProjectSource.StripLineComments(settingsText);
            foreach (Match match in IncludeBuildRegex().Matches(text))
            {
                var relative = match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar);
                directories.Add(Path.GetFullPath(Path.Combine(root, relative)));
            }
        }

        directories.Add(Path.Combine(root, "buildSrc"));
        var conventions = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var loaded = await LoadConventionBuildAsync(directory, cancellationToken).ConfigureAwait(false);
            foreach (var (id, effective) in loaded)
            {
                conventions[id] = effective;
            }
        }

        return conventions;
    }

    private static async Task<Dictionary<string, IReadOnlyCollection<string>>> LoadConventionBuildAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var registrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        var buildFile = ProjectSource.FindBuildFile(directory);
        var buildText = await ProjectSource.ReadTextAsync(buildFile, cancellationToken).ConfigureAwait(false);
        if (buildText is not null)
        {
            registrations = ParseRegistrations(ProjectSource.StripLineComments(buildText));
        }

        if (registrations.Count == 0)
        {
            return result;
        }

        var classToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, implementationClass) in registrations)
        {
            classToId[SimpleClassName(implementationClass)] = id;
        }

        var sources = FindClassSources(Path.Combine(directory, "src"));
        var direct = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, implementationClass) in registrations)
        {
            var applies = new List<string>();
            if (sources.TryGetValue(SimpleClassName(implementationClass), out var source))
            {
                var sourceText = await ProjectSource.ReadTextAsync(source, cancellationToken).ConfigureAwait(false);
                if (sourceText is not null)
                {
                    applies = ParseApplies(ProjectSource.StripLineComments(sourceText), classToId);
                }
            }

            direct[id] = applies;
        }

        foreach (var id in direct.Keys)
        {
            var effective = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExpandConvention(id, direct, effective, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            result[id] = effective;
        }

        return result;
    }

    private static Dictionary<string, string> ParseRegistrations(string buildText)
    {
        var registrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = ProjectSource.ExtractBlockBody(buildText, "gradlePlugin");
        if (block is null)
        {
            return registrations;
        }

        string? pendingId = null;
        foreach (Match match in RegistrationDirectiveRegex().Matches(block))
        {
            if (match.Groups["id"].Success)
            {
                pendingId = match.Groups["id"].Value;
                continue;
            }

            if (pendingId is not null && match.Groups["class"].Success)
            {
                registrations[pendingId] = match.Groups["class"].Value;
                pendingId = null;
            }
        }

        return registrations;
    }

    private static Dictionary<string, string> FindClassSources(string sourceRoot)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(sourceRoot))
        {
            return sources;
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.kt", SearchOption.AllDirectories))
        {
            sources.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.java", SearchOption.AllDirectories))
        {
            sources.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        }

        return sources;
    }

    private static List<string> ParseApplies(string sourceText, Dictionary<string, string> classToId)
    {
        var applies = new List<string>();
        foreach (Match match in AppliedPluginRegex().Matches(sourceText))
        {
            applies.Add(match.Groups["id"].Value);
        }

        foreach (Match match in AppliedPluginGroovyRegex().Matches(sourceText))
        {
            applies.Add(match.Groups["id"].Value);
        }

        foreach (Match match in AppliedPluginClassRegex().Matches(sourceText))
        {
            if (classToId.TryGetValue(SimpleClassName(match.Groups["name"].Value), out var id))
            {
                applies.Add(id);
            }
        }

        return applies.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ExpandConvention(
        string id,
        Dictionary<string, List<string>> direct,
        HashSet<string> effective,
        HashSet<string> visiting)
    {
        effective.Add(id);
        if (!visiting.Add(id))
        {
            return;
        }

        if (direct.TryGetValue(id, out var applies))
        {
            foreach (var applied in applies)
            {
                ExpandConvention(applied, direct, effective, visiting);
            }
        }

        visiting.Remove(id);
    }

    private static string SimpleClassName(string value)
    {
        var separator = value.LastIndexOfAny(['.', '$']);
        return separator < 0 ? value : value[(separator + 1)..];
    }

    [GeneratedRegex(
        @"includeBuild\s*\(\s*[""'](?<path>[^""']+)[""']\s*\)|includeBuild\s+[""'](?<path>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex IncludeBuildRegex();

    [GeneratedRegex(
        @"alias\s*\(\s*libs\s*\.\s*plugins\s*\.\s*(?<path>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)\s*\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PluginAliasRegex();

    [GeneratedRegex(
        @"alias\s*\(\s*libs\s*\.\s*plugins\s*\[\s*[""'](?<path>[^""']+)[""']\s*\]\s*\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PluginAliasBracketRegex();

    [GeneratedRegex(
        @"\bid\s*\(\s*[""'](?<id>[^""']+)[""']\s*\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PluginIdFunctionRegex();

    [GeneratedRegex(
        @"\bid\s+[""'](?<id>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PluginIdGroovyRegex();

    [GeneratedRegex(
        @"\bkotlin\s*\(\s*[""'](?<name>[^""']+)[""']\s*\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex KotlinFunctionRegex();

    [GeneratedRegex(
        @"\bversion\s*\(?\s*[""'](?<version>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionLiteralRegex();

    [GeneratedRegex(@"\bapply\s+false\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApplyFalseRegex();

    [GeneratedRegex(
        @"\bapply\s*\(\s*(?:plugin\s*=\s*)?[""'](?<id>[A-Za-z0-9_.\-]+)[""']\s*\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppliedPluginRegex();

    [GeneratedRegex(
        @"\bapply\s+plugin\s*:\s*[""'](?<id>[A-Za-z0-9_.\-]+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppliedPluginGroovyRegex();

    [GeneratedRegex(
        @"\bapply\s*\(\s*(?<name>[A-Za-z_][\w.]*)\s*::\s*class(?:\s*\.\s*java)?\s*\)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppliedPluginClassRegex();

    [GeneratedRegex(
        @"\bid\s*=\s*[""'](?<id>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CatalogPluginIdRegex();

    [GeneratedRegex(
        @"\bversion\s*\.\s*ref\s*=\s*[""'](?<ref>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CatalogVersionRefRegex();

    [GeneratedRegex(
        @"\bversion\s*=\s*[""'](?<version>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CatalogVersionLiteralRegex();

    [GeneratedRegex(
        @"\bid\s*(?:=\s*|:\s*)?[""'](?<id>[^""']+)[""']|\bimplementationClass\s*(?:=\s*|:\s*)?[""'](?<class>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RegistrationDirectiveRegex();

    private sealed record CatalogPlugin(string Id, string? Version);
}
