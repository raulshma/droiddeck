using System.Text.Json;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// The outcome of reading a plugin manifest from disk.
/// </summary>
/// <param name="Manifest">The manifest, or <see langword="null"/> when it could not be read.</param>
/// <param name="Validation">The issues found while reading the manifest.</param>
public sealed record PluginManifestReadResult(PluginManifest? Manifest, PluginValidationResult Validation)
{
    /// <summary>Gets a value indicating whether a manifest was read successfully.</summary>
    public bool Succeeded => Manifest is not null && Validation.IsValid;
}

/// <summary>
/// Reads <c>plugin.json</c> manifests from plugin directories.
/// </summary>
public static class PluginManifestReader
{
    /// <summary>The manifest file name expected in every plugin directory.</summary>
    public const string FileName = "plugin.json";

    /// <summary>Gets the manifest path for a plugin directory.</summary>
    public static string GetManifestPath(string pluginDirectory)
        => Path.Combine(pluginDirectory, FileName);

    /// <summary>
    /// Reads and deserializes the manifest in a plugin directory.
    /// </summary>
    public static async Task<PluginManifestReadResult> ReadAsync(
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        var validation = new PluginValidationResult();
        var manifestPath = GetManifestPath(pluginDirectory);
        if (!File.Exists(manifestPath))
        {
            validation.Error("manifest.missing", $"No {FileName} was found in '{pluginDirectory}'.");
            return new PluginManifestReadResult(null, validation);
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer
                .DeserializeAsync(stream, PluginJsonContext.Default.PluginManifest, cancellationToken)
                .ConfigureAwait(false);

            if (manifest is null)
            {
                validation.Error("manifest.empty", $"'{manifestPath}' did not contain a plugin manifest.");
                return new PluginManifestReadResult(null, validation);
            }

            return new PluginManifestReadResult(manifest, validation);
        }
        catch (JsonException exception)
        {
            validation.Error("manifest.invalidJson", $"Could not parse '{manifestPath}': {exception.Message}");
        }
        catch (IOException exception)
        {
            validation.Error("manifest.unreadable", $"Could not read '{manifestPath}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            validation.Error("manifest.unreadable", $"Could not read '{manifestPath}': {exception.Message}");
        }

        return new PluginManifestReadResult(null, validation);
    }
}
