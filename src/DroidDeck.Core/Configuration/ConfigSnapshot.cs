using System.Text.Json.Nodes;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// The configuration layers known to the host, in increasing precedence.
/// </summary>
public enum ConfigScope
{
    /// <summary>Embedded defaults.</summary>
    BuiltInDefaults,

    /// <summary>The per-user global settings file.</summary>
    Global,

    /// <summary>The active profile.</summary>
    Profile,

    /// <summary>Opt-in team-shared project configuration.</summary>
    TeamProject,

    /// <summary>Machine-local project overrides.</summary>
    LocalProject,

    /// <summary>A run configuration.</summary>
    RunConfiguration,

    /// <summary>A device or device group override.</summary>
    Device,

    /// <summary>A session or command-line override.</summary>
    Session,
}

/// <summary>
/// The layer that supplied an effective value.
/// </summary>
/// <param name="Scope">The contributing scope.</param>
/// <param name="Origin">The file, profile, or other origin within the scope.</param>
public sealed record ConfigSource(ConfigScope Scope, string? Origin = null)
{
    /// <summary>Gets the built-in defaults source.</summary>
    public static ConfigSource Defaults { get; } = new(ConfigScope.BuiltInDefaults, "defaults.json");

    /// <summary>Gets a global settings source for a file.</summary>
    public static ConfigSource Global(string path) => new(ConfigScope.Global, path);

    /// <summary>Gets a user-facing source name.</summary>
    public string DisplayName => Scope switch
    {
        ConfigScope.BuiltInDefaults => "Default",
        ConfigScope.Global => "Global",
        ConfigScope.Profile => "Profile",
        ConfigScope.TeamProject => "Team project",
        ConfigScope.LocalProject => "Project",
        ConfigScope.RunConfiguration => "Run configuration",
        ConfigScope.Device => "Device",
        ConfigScope.Session => "Session",
        _ => Scope.ToString(),
    };
}

/// <summary>
/// An immutable view of effective settings and where each value came from.
/// </summary>
public sealed class ConfigSnapshot
{
    private readonly IReadOnlyDictionary<string, ConfigSource> _sources;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ConfigSource>> _contributions;
    private readonly object _valueCacheLock = new();
    private readonly Dictionary<(string Path, Type Type), object?> _valueCache = [];

    internal ConfigSnapshot(
        AppSettings settings,
        JsonObject effective,
        IReadOnlyDictionary<string, ConfigSource> sources,
        IReadOnlyDictionary<string, IReadOnlyList<ConfigSource>> contributions)
    {
        Settings = settings;
        Effective = effective;
        _sources = sources;
        _contributions = contributions;
    }

    /// <summary>Gets the typed effective settings.</summary>
    public AppSettings Settings { get; }

    /// <summary>Gets the merged JSON document.</summary>
    public JsonObject Effective { get; }

    /// <summary>Gets the schema version of the effective document.</summary>
    public int SchemaVersion => Settings.SchemaVersion;

    /// <summary>Gets the source that supplied an effective leaf value.</summary>
    public ConfigSource GetSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _sources.TryGetValue(path, out var source) ? source : ConfigSource.Defaults;
    }

    /// <summary>Returns whether the value is overridden above the built-in defaults.</summary>
    public bool IsOverridden(string path) => GetSource(path).Scope != ConfigScope.BuiltInDefaults;

    /// <summary>Gets a JSON value by dotted path.</summary>
    public JsonNode? GetValue(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonNode? current = Effective;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj ||
                !SettingsDocument.TryGetProperty(obj, segment, out var value))
            {
                return null;
            }

            current = value;
        }

        return current;
    }

    /// <summary>
    /// Gets every layer that contributed the effective value, ordered from the lowest
    /// precedence to the highest; the last entry is the winning source. Built-in
    /// defaults are not listed; unknown paths return an empty list.
    /// </summary>
    public IReadOnlyList<ConfigSource> GetContributions(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _contributions.TryGetValue(path, out var contributors) ? contributors : [];
    }

    /// <summary>Gets all source-map entries.</summary>
    public IReadOnlyDictionary<string, ConfigSource> GetSourceMap() => _sources;

    internal bool TryGetCached<T>(string path, out T? value)
    {
        lock (_valueCacheLock)
        {
            if (_valueCache.TryGetValue((path, typeof(T)), out var cached))
            {
                value = (T?)cached;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal void CacheValue<T>(string path, T? value)
    {
        lock (_valueCacheLock)
        {
            _valueCache[(path, typeof(T))] = value;
        }
    }
}
