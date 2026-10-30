using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugins;

/// <summary>
/// Persists host-managed ordering for plugin UI surfaces (session toolbars,
/// status bar, command menus) as one JSON document in the data directory. Each
/// surface stores the sequence of item identifiers as they were last shown, so
/// the arrangement survives restarts even when plugins load in a different
/// order: items keep their persisted position first, new items append after
/// them, and removed identifiers are pruned silently on the next save.
/// </summary>
public sealed class PluginUiOrderStore : IDisposable
{
    /// <summary>The surface key for session and device toolbar actions.</summary>
    public const string ToolbarSurface = "toolbar";

    /// <summary>The surface key for status bar items.</summary>
    public const string StatusBarSurface = "statusBar";

    /// <summary>The surface key for command menu and palette entries.</summary>
    public const string CommandsSurface = "commands";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, List<string>> _surfaces = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates a store over the ordering document at <paramref name="filePath"/>.</summary>
    public PluginUiOrderStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    /// <summary>Gets the file the ordering persists to.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Returns the persisted identifier sequence for a surface. Unknown
    /// surfaces and unreadable files yield an empty sequence.
    /// </summary>
    public IReadOnlyList<string> Load(string surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        lock (_sync)
        {
            EnsureLoaded();
            return _surfaces.TryGetValue(surface, out var ids) ? ids.ToArray() : [];
        }
    }

    /// <summary>
    /// Orders entries by their persisted position. Entries without a persisted
    /// position keep the incoming order after the persisted ones, so callers
    /// pass catalog-sorted entries and the result stays stable.
    /// </summary>
    public IReadOnlyList<TEntry> Sort<TEntry>(
        string surface,
        IEnumerable<TEntry> entries,
        Func<TEntry, string> id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(id);

        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_sync)
        {
            EnsureLoaded();
            if (_surfaces.TryGetValue(surface, out var ids))
            {
                for (var index = 0; index < ids.Count; index++)
                {
                    positions.TryAdd(ids[index], index);
                }
            }
        }

        return entries
            .Select((entry, index) => (Entry: entry, Index: index, Position: positions.TryGetValue(id(entry), out var position) ? position : int.MaxValue))
            .OrderBy(candidate => candidate.Position)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Entry)
            .ToArray();
    }

    /// <summary>
    /// Persists the identifier sequence of a surface, replacing whatever was
    /// recorded before, so identifiers that no longer exist are pruned. The
    /// in-memory change applies even when the file cannot be written; the next
    /// save retries. The snapshot and the file write are serialized together,
    /// so two concurrent saves can never interleave their writes and leave a
    /// stale surface sequence in the file.
    /// </summary>
    public async Task SaveAsync(
        string surface,
        IReadOnlyList<string> orderedIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentNullException.ThrowIfNull(orderedIds);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, List<string>> snapshot;
            lock (_sync)
            {
                EnsureLoaded();
                _surfaces[surface] = [.. orderedIds.Where(static id => !string.IsNullOrWhiteSpace(id))];
                snapshot = _surfaces.ToDictionary(
                    pair => pair.Key,
                    pair => new List<string>(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var document = new SavedOrder
            {
                Surfaces = snapshot.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray()),
            };
            await File.WriteAllTextAsync(
                FilePath,
                JsonSerializer.Serialize(document, Options),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Persists the identifier sequence of a surface only when it differs from
    /// the sequence recorded before, so re-rendering an unchanged surface never
    /// rewrites the file. Unknown surfaces compare against an empty sequence;
    /// IO failures surface to the caller, as with <see cref="SaveAsync"/>.
    /// </summary>
    public async Task SaveIfChangedAsync(
        string surface,
        IEnumerable<string> orderedIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentNullException.ThrowIfNull(orderedIds);

        var ids = orderedIds.ToArray();
        lock (_sync)
        {
            EnsureLoaded();
            var persisted = _surfaces.TryGetValue(surface, out var stored) ? stored : [];
            if (persisted.SequenceEqual(ids, StringComparer.Ordinal))
            {
                return;
            }
        }

        await SaveAsync(surface, ids, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _saveGate.Dispose();
    }

    private sealed class SavedOrder
    {
        [JsonPropertyName("surfaces")]
        public Dictionary<string, string[]>? Surfaces { get; init; }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(FilePath))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(FilePath);
            var state = JsonSerializer.Deserialize<SavedOrder>(stream, Options);
            if (state?.Surfaces is null)
            {
                return;
            }

            foreach (var pair in state.Surfaces)
            {
                if (pair.Value is { Length: > 0 })
                {
                    _surfaces[pair.Key] = [.. pair.Value.Where(static id => !string.IsNullOrWhiteSpace(id))];
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt ordering file must never block the shell; treat every
            // surface as unordered and overwrite the file on the next save.
            _surfaces.Clear();
        }
    }
}
