using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>The kind of problem a declared host version range has.</summary>
public enum PluginHostRangeIssueKind
{
    /// <summary>The minimum host version is not a semantic version.</summary>
    MinimumHostVersionInvalid,

    /// <summary>The maximum host version is not a semantic version.</summary>
    MaximumHostVersionInvalid,

    /// <summary>The host is older than the declared minimum.</summary>
    HostTooOld,

    /// <summary>The host is newer than the declared maximum.</summary>
    HostTooNew,
}

/// <summary>One problem with a plugin's host version range.</summary>
/// <param name="Kind">The kind of problem.</param>
/// <param name="DeclaredVersion">The version text declared by the plugin.</param>
/// <param name="BoundaryVersion">The normalized boundary version, when it parsed.</param>
public sealed record PluginHostRangeIssue(
    PluginHostRangeIssueKind Kind,
    string DeclaredVersion,
    string? BoundaryVersion);

/// <summary>Why a declared plugin dependency is not satisfied.</summary>
public enum PluginDependencyIssueKind
{
    /// <summary>No installed plugin has the required id.</summary>
    Missing,

    /// <summary>The installed plugin is older than the declared minimum.</summary>
    VersionTooOld,
}

/// <summary>One plugin dependency that is not satisfied.</summary>
/// <param name="Kind">The kind of problem.</param>
/// <param name="DependencyId">The required plugin identifier.</param>
/// <param name="RequiredVersion">The declared minimum version.</param>
/// <param name="InstalledVersion">The installed version, or <see langword="null"/> when the plugin is missing.</param>
public sealed record PluginDependencyIssue(
    PluginDependencyIssueKind Kind,
    string DependencyId,
    string RequiredVersion,
    string? InstalledVersion);

/// <summary>
/// The result of evaluating a plugin's API version, host version range, and
/// dependencies against one host. Callers keep their own wording because the
/// manifest validator, catalog browser, installer, and loader explain the same
/// rules in their own contexts; the rules themselves live here.
/// </summary>
/// <param name="Api">The plugin API compatibility.</param>
/// <param name="HostRangeIssues">The host version range problems, in declaration order.</param>
/// <param name="DependencyIssues">The unsatisfied dependencies, in declaration order.</param>
/// <param name="HostVersion">The normalized host version, or the input when it did not parse.</param>
public sealed record PluginCompatibilityVerdict(
    ApiCompatibility Api,
    IReadOnlyList<PluginHostRangeIssue> HostRangeIssues,
    IReadOnlyList<PluginDependencyIssue> DependencyIssues,
    string HostVersion)
{
    /// <summary>Gets a value indicating whether the plugin can load on this host.</summary>
    public bool IsCompatible =>
        Api is ApiCompatibility.Compatible or ApiCompatibility.MinorMismatch &&
        HostRangeIssues.Count == 0 &&
        DependencyIssues.Count == 0;

    /// <summary>Gets a value indicating whether the plugin loads with an API warning.</summary>
    public bool HasWarning => Api == ApiCompatibility.MinorMismatch;
}

/// <summary>
/// The one host compatibility and dependency policy. The manifest validator,
/// catalog browser, catalog installer, and plugin loader all delegate here so a
/// change to the API range, host version range, or dependency rule is made once.
/// </summary>
public static class PluginCompatibilityPolicy
{
    /// <summary>
    /// Evaluates a manifest's API version, host version range, and dependencies
    /// against a host and the versions of the plugins that are available to it.
    /// </summary>
    /// <param name="manifest">The manifest to evaluate.</param>
    /// <param name="hostVersion">The host version to compare against.</param>
    /// <param name="availablePluginVersions">
    /// Installed or loaded plugin versions keyed by plugin id; when omitted,
    /// dependencies are not resolved.
    /// </param>
    /// <param name="hostApiVersion">The host plugin API version; defaults to the running API.</param>
    public static PluginCompatibilityVerdict Evaluate(
        PluginManifest manifest,
        string hostVersion,
        IReadOnlyDictionary<string, string>? availablePluginVersions = null,
        string? hostApiVersion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Evaluate(
            manifest.ApiVersion,
            manifest.MinHostVersion,
            manifest.MaxHostVersion,
            manifest.Dependencies,
            hostVersion,
            availablePluginVersions,
            hostApiVersion);
    }

    /// <summary>
    /// Evaluates a catalog version's API version and host version range against a host.
    /// </summary>
    /// <param name="apiVersion">The plugin API version.</param>
    /// <param name="minHostVersion">The declared minimum host version, if any.</param>
    /// <param name="maxHostVersion">The declared maximum host version, if any.</param>
    /// <param name="hostVersion">The host version to compare against.</param>
    /// <param name="hostApiVersion">The host plugin API version; defaults to the running API.</param>
    public static PluginCompatibilityVerdict EvaluateVersion(
        string? apiVersion,
        string? minHostVersion,
        string? maxHostVersion,
        string hostVersion,
        string? hostApiVersion = null)
        => Evaluate(
            apiVersion: apiVersion,
            minHostVersion: minHostVersion,
            maxHostVersion: maxHostVersion,
            dependencies: null,
            hostVersion: hostVersion,
            availablePluginVersions: null,
            hostApiVersion: hostApiVersion);

    /// <summary>
    /// Evaluates declared dependencies against the versions of the plugins that
    /// are available to the host. Invalid minimum versions are ignored, matching
    /// the loader and installer, which reject them during manifest validation.
    /// </summary>
    /// <param name="dependencies">The declared dependencies.</param>
    /// <param name="availablePluginVersions">Available plugin versions keyed by plugin id.</param>
    public static IReadOnlyList<PluginDependencyIssue> EvaluateDependencies(
        IReadOnlyList<PluginDependency> dependencies,
        IReadOnlyDictionary<string, string> availablePluginVersions)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(availablePluginVersions);

        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in availablePluginVersions)
        {
            available[id] = version;
        }

        var issues = new List<PluginDependencyIssue>();
        foreach (var dependency in dependencies)
        {
            if (!available.TryGetValue(dependency.Id, out var installedVersion))
            {
                issues.Add(new PluginDependencyIssue(
                    PluginDependencyIssueKind.Missing,
                    dependency.Id,
                    dependency.MinVersion,
                    InstalledVersion: null));
                continue;
            }

            if (SemanticVersion.TryParse(dependency.MinVersion, out var minimum) &&
                SemanticVersion.TryParse(installedVersion, out var actual) &&
                actual < minimum)
            {
                issues.Add(new PluginDependencyIssue(
                    PluginDependencyIssueKind.VersionTooOld,
                    dependency.Id,
                    dependency.MinVersion,
                    installedVersion));
            }
        }

        return issues;
    }

    /// <summary>Evaluates the shared API and host version range rules.</summary>
    private static PluginCompatibilityVerdict Evaluate(
        string? apiVersion,
        string? minHostVersion,
        string? maxHostVersion,
        IReadOnlyList<PluginDependency>? dependencies,
        string hostVersion,
        IReadOnlyDictionary<string, string>? availablePluginVersions,
        string? hostApiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);

        var api = PluginApi.CheckCompatibility(apiVersion, hostApiVersion);
        var hostIssues = new List<PluginHostRangeIssue>();
        var normalizedHostVersion = hostVersion;
        if (SemanticVersion.TryParse(hostVersion, out var host))
        {
            normalizedHostVersion = host.ToString();
            AddMinimumIssue(minHostVersion, host, hostIssues);
            AddMaximumIssue(maxHostVersion, host, hostIssues);
        }

        IReadOnlyList<PluginDependencyIssue> dependencyIssues =
            dependencies is null || availablePluginVersions is null
                ? []
                : EvaluateDependencies(dependencies, availablePluginVersions);

        return new PluginCompatibilityVerdict(api, hostIssues, dependencyIssues, normalizedHostVersion);
    }

    private static void AddMinimumIssue(
        string? minHostVersion,
        SemanticVersion host,
        List<PluginHostRangeIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(minHostVersion))
        {
            return;
        }

        if (!SemanticVersion.TryParse(minHostVersion, out var minimum))
        {
            issues.Add(new PluginHostRangeIssue(
                PluginHostRangeIssueKind.MinimumHostVersionInvalid,
                minHostVersion,
                BoundaryVersion: null));
        }
        else if (host < minimum)
        {
            issues.Add(new PluginHostRangeIssue(
                PluginHostRangeIssueKind.HostTooOld,
                minHostVersion,
                minimum.ToString()));
        }
    }

    private static void AddMaximumIssue(
        string? maxHostVersion,
        SemanticVersion host,
        List<PluginHostRangeIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(maxHostVersion))
        {
            return;
        }

        if (!SemanticVersion.TryParse(maxHostVersion, out var maximum))
        {
            issues.Add(new PluginHostRangeIssue(
                PluginHostRangeIssueKind.MaximumHostVersionInvalid,
                maxHostVersion,
                BoundaryVersion: null));
        }
        else if (host > maximum)
        {
            issues.Add(new PluginHostRangeIssue(
                PluginHostRangeIssueKind.HostTooNew,
                maxHostVersion,
                maximum.ToString()));
        }
    }
}
