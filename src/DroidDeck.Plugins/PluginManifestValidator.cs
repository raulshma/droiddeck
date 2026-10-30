using System.Text.RegularExpressions;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// Validates plugin manifests before any plugin assembly is loaded.
/// </summary>
public static partial class PluginManifestValidator
{
    [GeneratedRegex(@"^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    /// <summary>
    /// Validates a manifest and the files it references.
    /// </summary>
    /// <param name="manifest">The manifest to validate.</param>
    /// <param name="pluginDirectory">The directory that contains the manifest and plugin files.</param>
    /// <param name="hostVersion">The host version to compare against; defaults to the running application version.</param>
    public static PluginValidationResult Validate(
        PluginManifest manifest,
        string pluginDirectory,
        string? hostVersion = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var result = new PluginValidationResult();
        if (!Directory.Exists(pluginDirectory))
        {
            result.Error("plugin.directory.missing", $"The plugin directory '{pluginDirectory}' does not exist.");
            return result;
        }

        ValidateIdentity(manifest, result);
        ValidateApiCompatibility(manifest, hostVersion ?? AppInfo.Version, result);
        ValidateEntryPoint(manifest, pluginDirectory, result);
        ValidateCapabilities(manifest, result);
        ValidateDependencies(manifest, result);
        ValidateOptionalFiles(manifest, pluginDirectory, result);
        return result;
    }

    private static void ValidateIdentity(PluginManifest manifest, PluginValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            result.Error("manifest.id.required", "The plugin id is required.");
        }
        else if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            result.Error(
                "manifest.id.invalid",
                $"The plugin id '{manifest.Id}' must be lowercase and contain only letters, digits, dots, and dashes.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            result.Error("manifest.name.required", "The plugin name is required.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            result.Error(
                "manifest.version.invalid",
                $"The plugin version '{manifest.Version}' is not a semantic version.");
        }
    }

    private static void ValidateApiCompatibility(
        PluginManifest manifest,
        string hostVersion,
        PluginValidationResult result)
    {
        // The plugin ABI is versioned independently of the app; min/maxHostVersion cover app versions.
        var compatibility = PluginApi.CheckCompatibility(manifest.ApiVersion);
        switch (compatibility)
        {
            case ApiCompatibility.Compatible:
                break;
            case ApiCompatibility.MinorMismatch:
                result.Warning(
                    "manifest.apiVersion.minorMismatch",
                    $"The plugin targets API {manifest.ApiVersion}, but the host implements {PluginApi.Version}.");
                break;
            case ApiCompatibility.MajorMismatch:
                result.Error(
                    "manifest.apiVersion.majorMismatch",
                    $"The plugin targets API {manifest.ApiVersion}, which is incompatible with host API {PluginApi.Version}.");
                break;
            default:
                result.Error(
                    "manifest.apiVersion.invalid",
                    $"The plugin API version '{manifest.ApiVersion}' is not valid.");
                break;
        }

        if (!SemanticVersion.TryParse(hostVersion, out var host))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest.MinHostVersion))
        {
            if (!SemanticVersion.TryParse(manifest.MinHostVersion, out var minimum))
            {
                result.Error(
                    "manifest.minHostVersion.invalid",
                    $"The minimum host version '{manifest.MinHostVersion}' is not a semantic version.");
            }
            else if (host < minimum)
            {
                result.Error(
                    "manifest.minHostVersion.newerThanHost",
                    $"The plugin requires host version {minimum} or newer; this host is {host}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.MaxHostVersion))
        {
            if (!SemanticVersion.TryParse(manifest.MaxHostVersion, out var maximum))
            {
                result.Error(
                    "manifest.maxHostVersion.invalid",
                    $"The maximum host version '{manifest.MaxHostVersion}' is not a semantic version.");
            }
            else if (host > maximum)
            {
                result.Error(
                    "manifest.maxHostVersion.olderThanHost",
                    $"The plugin supports host versions up to {maximum}; this host is {host}.");
            }
        }
    }

    private static void ValidateEntryPoint(
        PluginManifest manifest,
        string pluginDirectory,
        PluginValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            result.Error("manifest.entryAssembly.required", "The entry assembly is required.");
        }
        else if (!IsSimpleFileName(manifest.EntryAssembly) ||
            !string.Equals(Path.GetExtension(manifest.EntryAssembly), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            result.Error(
                "manifest.entryAssembly.invalid",
                $"The entry assembly '{manifest.EntryAssembly}' must be a .dll file name inside the plugin directory.");
        }
        else if (!File.Exists(Path.Combine(pluginDirectory, manifest.EntryAssembly)))
        {
            result.Error(
                "manifest.entryAssembly.missing",
                $"The entry assembly '{manifest.EntryAssembly}' was not found in '{pluginDirectory}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            result.Error("manifest.entryType.required", "The entry type is required.");
        }
    }

    private static void ValidateCapabilities(PluginManifest manifest, PluginValidationResult result)
    {
        foreach (var unknown in manifest.UnknownCapabilities)
        {
            result.Warning(
                "manifest.capabilities.unknown",
                $"The capability '{unknown}' is not recognized by this host and was ignored.");
        }
    }

    private static void ValidateDependencies(PluginManifest manifest, PluginValidationResult result)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in manifest.Dependencies ?? [])
        {
            if (string.IsNullOrWhiteSpace(dependency.Id) || !PluginIdPattern().IsMatch(dependency.Id))
            {
                result.Error(
                    "manifest.dependencies.invalidId",
                    $"The dependency id '{dependency.Id}' is not a valid plugin id.");
            }
            else if (string.Equals(dependency.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                result.Error("manifest.dependencies.self", "A plugin cannot depend on itself.");
            }
            else if (!ids.Add(dependency.Id))
            {
                result.Error(
                    "manifest.dependencies.duplicate",
                    $"The dependency '{dependency.Id}' is declared more than once.");
            }

            if (!SemanticVersion.TryParse(dependency.MinVersion, out _))
            {
                result.Error(
                    "manifest.dependencies.invalidVersion",
                    $"The dependency '{dependency.Id}' has an invalid minimum version '{dependency.MinVersion}'.");
            }
        }
    }

    private static void ValidateOptionalFiles(
        PluginManifest manifest,
        string pluginDirectory,
        PluginValidationResult result)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Icon))
        {
            if (!IsSimpleFileName(manifest.Icon))
            {
                result.Warning(
                    "manifest.icon.invalid",
                    $"The icon '{manifest.Icon}' must be a file name inside the plugin directory.");
            }
            else if (!File.Exists(Path.Combine(pluginDirectory, manifest.Icon)))
            {
                result.Warning(
                    "manifest.icon.missing",
                    $"The icon '{manifest.Icon}' was not found in '{pluginDirectory}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.Homepage) &&
            (!Uri.TryCreate(manifest.Homepage, UriKind.Absolute, out var homepage) ||
                (homepage.Scheme != Uri.UriSchemeHttp && homepage.Scheme != Uri.UriSchemeHttps)))
        {
            result.Warning(
                "manifest.homepage.invalid",
                $"The homepage '{manifest.Homepage}' is not an absolute HTTP or HTTPS URL.");
        }
    }

    private static bool IsSimpleFileName(string value)
        => !Path.IsPathRooted(value) &&
            string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) &&
            !value.Contains("..", StringComparison.Ordinal);
}
