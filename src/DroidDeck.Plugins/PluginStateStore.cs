using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugins;

/// <summary>
/// Persists which plugins the user disabled in <c>state.json</c> next to the
/// plugin directories. Disabled plugins are skipped on the next launch because
/// plugins are not hot-loaded; enable and disable complete on restart.
/// </summary>
public sealed class PluginStateStore
{
    /// <summary>The state file name inside the plugin root.</summary>
    public const string FileName = "state.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly HashSet<string> _disabledIds = new(StringComparer.OrdinalIgnoreCase);

    private PluginStateStore(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Gets the file the state persists to.</summary>
    public string FilePath { get; }

    /// <summary>Gets the disabled plugin identifiers in their persisted order.</summary>
    public IReadOnlyList<string> DisabledIds
    {
        get
        {
            lock (_sync)
            {
                return _disabledIds.ToArray();
            }
        }
    }

    /// <summary>Loads the state for a plugin root; a missing or unreadable file yields an empty store.</summary>
    public static async Task<PluginStateStore> LoadAsync(string pluginsRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        var store = new PluginStateStore(Path.Combine(pluginsRoot, FileName));
        if (!File.Exists(store.FilePath))
        {
            return store;
        }

        try
        {
            await using var stream = File.OpenRead(store.FilePath);
            var state = await JsonSerializer.DeserializeAsync<SavedState>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
            if (state?.Disabled is not null)
            {
                foreach (var id in state.Disabled)
                {
                    store._disabledIds.Add(id);
                }
            }

            return store;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt state file must never block the manager; treat every
            // plugin as enabled and overwrite the file on the next change.
            return new PluginStateStore(store.FilePath);
        }
    }

    /// <summary>Returns whether a plugin is persisted as disabled.</summary>
    public bool IsDisabled(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _disabledIds.Contains(pluginId);
        }
    }

    /// <summary>
    /// Updates the disabled flag for one plugin and persists it. The in-memory
    /// change applies even when the file cannot be written; the next change
    /// retries the save.
    /// </summary>
    public async Task SetDisabledAsync(string pluginId, bool disabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        List<string> snapshot;
        lock (_sync)
        {
            if (disabled)
            {
                _disabledIds.Add(pluginId);
            }
            else
            {
                _disabledIds.Remove(pluginId);
            }

            snapshot = [.. _disabledIds];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(
            FilePath,
            JsonSerializer.Serialize(new SavedState(snapshot), Options),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record SavedState(IReadOnlyList<string> Disabled);
}
