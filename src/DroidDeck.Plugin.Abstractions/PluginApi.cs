namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// Describes how compatible a plugin's API version is with the host.
/// </summary>
public enum ApiCompatibility
{
    /// <summary>The plugin targets the host API exactly.</summary>
    Compatible,

    /// <summary>The major versions match, but the minor versions differ.</summary>
    MinorMismatch,

    /// <summary>The plugin targets a different major API version.</summary>
    MajorMismatch,

    /// <summary>One or both versions could not be parsed.</summary>
    Invalid,
}

/// <summary>
/// Plugin API version helpers. The plugin ABI is stable within a major version.
/// </summary>
public static class PluginApi
{
    /// <summary>The API version implemented by this build of the abstractions.</summary>
    public const string Version = "1.0";

    /// <summary>Gets the current API version as a parsed value.</summary>
    public static SemanticVersion HostVersion { get; } = ParseApiVersion(Version);

    /// <summary>
    /// Checks a plugin's declared API version against the host API version.
    /// </summary>
    public static ApiCompatibility CheckCompatibility(string? pluginApiVersion, string? hostApiVersion = null)
    {
        if (!TryParseApiVersion(pluginApiVersion, out var plugin) ||
            !TryParseApiVersion(hostApiVersion ?? Version, out var host))
        {
            return ApiCompatibility.Invalid;
        }

        if (plugin.Major != host.Major)
        {
            return ApiCompatibility.MajorMismatch;
        }

        return plugin.Minor == host.Minor ? ApiCompatibility.Compatible : ApiCompatibility.MinorMismatch;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the plugin can load. Minor mismatches load with a warning.
    /// </summary>
    public static bool IsCompatible(string? pluginApiVersion, string? hostApiVersion = null)
        => CheckCompatibility(pluginApiVersion, hostApiVersion) is ApiCompatibility.Compatible or ApiCompatibility.MinorMismatch;

    /// <summary>
    /// Parses an API version. API versions are written as <c>major.minor</c> and are
    /// normalized to a three-part semantic version.
    /// </summary>
    internal static bool TryParseApiVersion(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        return SemanticVersion.TryParse(text, out version) ||
            SemanticVersion.TryParse(text + ".0", out version);
    }

    private static SemanticVersion ParseApiVersion(string value)
        => TryParseApiVersion(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid API version.");
}
