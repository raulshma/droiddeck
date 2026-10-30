using System.Text.Json;
using System.Text.Json.Serialization;
using DroidDeck.Plugin.Abstractions.Rendering;

namespace DroidDeck.Plugins.Rendering;

/// <summary>
/// Persists the preferred renderer per kind in <c>renderers.json</c> next to the
/// other application data. A missing or corrupt file yields defaults.
/// </summary>
public sealed class RendererPreferences
{
    /// <summary>The file name used inside the state directory.</summary>
    public const string FileName = "renderers.json";

    private static readonly RendererPreferencesJsonContext Json = new(
        new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

    private readonly object _sync = new();
    private readonly Dictionary<RendererKind, string> _defaults = [];

    private RendererPreferences(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Gets the preferences file path.</summary>
    public string FilePath { get; }

    /// <summary>Gets a snapshot of the remembered renderer per kind.</summary>
    public IReadOnlyDictionary<RendererKind, string> Defaults
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<RendererKind, string>(_defaults);
            }
        }
    }

    /// <summary>Loads the preferences from a state directory.</summary>
    public static async Task<RendererPreferences> LoadAsync(
        string stateDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var preferences = new RendererPreferences(Path.Combine(stateDirectory, FileName));
        if (!File.Exists(preferences.FilePath))
        {
            return preferences;
        }

        try
        {
            await using var stream = File.OpenRead(preferences.FilePath);
            var document = await JsonSerializer
                .DeserializeAsync(stream, Json.RendererPreferencesDocument, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pair in document?.Defaults ?? [])
            {
                if (Enum.TryParse<RendererKind>(pair.Key, ignoreCase: true, out var kind) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                {
                    preferences._defaults[kind] = pair.Value;
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new RendererPreferences(preferences.FilePath);
        }

        return preferences;
    }

    /// <summary>Gets the remembered renderer for a kind.</summary>
    public string? GetDefault(RendererKind kind)
    {
        lock (_sync)
        {
            return _defaults.TryGetValue(kind, out var id) ? id : null;
        }
    }

    /// <summary>Remembers a renderer for a kind, or clears the preference when empty.</summary>
    public Task SetDefaultAsync(
        RendererKind kind,
        string? rendererId,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> snapshot;
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(rendererId))
            {
                _defaults.Remove(kind);
            }
            else
            {
                _defaults[kind] = rendererId;
            }

            snapshot = _defaults.ToDictionary(
                pair => pair.Key.ToString(),
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        return SaveAsync(snapshot, cancellationToken);
    }

    /// <summary>Clears every remembered renderer.</summary>
    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _defaults.Clear();
        }

        return SaveAsync([], cancellationToken);
    }

    private async Task SaveAsync(Dictionary<string, string> defaults, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var document = new RendererPreferencesDocument { Defaults = defaults };
        var temporary = FilePath + ".tmp";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer
                .SerializeAsync(stream, document, Json.RendererPreferencesDocument, cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>The persisted renderer preference document.</summary>
public sealed class RendererPreferencesDocument
{
    /// <summary>Gets or sets the schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Gets or sets renderer identifiers keyed by kind name.</summary>
    public Dictionary<string, string> Defaults { get; set; } = [];
}

/// <summary>Source-generated JSON metadata for renderer preferences.</summary>
[JsonSerializable(typeof(RendererPreferencesDocument))]
internal sealed partial class RendererPreferencesJsonContext : JsonSerializerContext;
