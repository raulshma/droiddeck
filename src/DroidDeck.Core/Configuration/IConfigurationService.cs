using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Configuration;

/// <summary>The kind of configuration change.</summary>
public enum ConfigChangeKind
{
    /// <summary>A value was set.</summary>
    Set,

    /// <summary>A value was reset to a lower layer.</summary>
    Reset,
}

/// <summary>Describes a configuration change.</summary>
public sealed class ConfigChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for a configuration change.</summary>
    public ConfigChangedEventArgs(string path, ConfigScope scope, ConfigSource source, ConfigChangeKind kind)
    {
        Path = path;
        Scope = scope;
        Source = source;
        Kind = kind;
    }

    /// <summary>Gets the dotted setting path.</summary>
    public string Path { get; }

    /// <summary>Gets the scope that changed.</summary>
    public ConfigScope Scope { get; }

    /// <summary>Gets the new effective source.</summary>
    public ConfigSource Source { get; }

    /// <summary>Gets the kind of change.</summary>
    public ConfigChangeKind Kind { get; }
}

/// <summary>
/// A user-facing notice raised when settings could not be loaded cleanly.
/// </summary>
/// <param name="Message">The explanation shown in the UI.</param>
/// <param name="QuarantinedPath">The quarantine file, when one was created.</param>
/// <param name="RecoveredFromBackup">Whether the backup was restored.</param>
public sealed record SettingsLoadNotice(
    string Message,
    string? QuarantinedPath,
    bool RecoveredFromBackup);

/// <summary>
/// Resolves layered settings and reports the source of every effective value.
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// Gets the current immutable snapshot. Snapshots are replaced on every write, so
    /// consumers can cache against one snapshot; repeated reads against a snapshot are
    /// cached and must stay within the 1 ms per-resolution budget.
    /// </summary>
    ConfigSnapshot Current { get; }

    /// <summary>Gets whether the settings file has been loaded.</summary>
    bool IsInitialized { get; }

    /// <summary>Gets a notice from the last load, if recovery was needed.</summary>
    SettingsLoadNotice? LastLoadNotice { get; }

    /// <summary>Loads settings from disk. Safe to call more than once.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets a value at the global scope.</summary>
    Task SetAsync<T>(string path, T value, ConfigScope scope = ConfigScope.Global, CancellationToken cancellationToken = default);

    /// <summary>Removes an override so the lower layer applies again.</summary>
    Task ResetAsync(string path, ConfigScope scope = ConfigScope.Global, CancellationToken cancellationToken = default);

    /// <summary>Gets the source of an effective value.</summary>
    ConfigSource GetSource(string path);

    /// <summary>
    /// Attaches the active project, activating the team-shared layer
    /// (<c>&lt;repo&gt;/.droiddeck/project.json</c>) and the local project override layer.
    /// Pass <see langword="null"/> to detach. Publishes a new snapshot; observe
    /// <see cref="GetChangeToken"/> for bulk invalidation.
    /// </summary>
    Task SetProjectContextAsync(ProjectContext? context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates the named profile layer, or deactivates it with <see langword="null"/>.
    /// The selection persists in the global scope.
    /// </summary>
    Task UseProfileAsync(string? profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the run-configuration layer with the given run configuration, or clears
    /// the layer with <see langword="null"/>. Publishes a new snapshot without per-path events.
    /// </summary>
    Task SetRunConfigurationAsync(RunConfiguration? configuration, CancellationToken cancellationToken = default);

    /// <summary>Applies raw key/value pairs to the session layer; values may be JSON or plain text.</summary>
    Task SetSessionOverridesAsync(IEnumerable<KeyValuePair<string, string>> overrides, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every session-layer override. Publishes a new snapshot without per-path
    /// events; observe <see cref="GetChangeToken"/> for bulk invalidation.
    /// </summary>
    Task ClearSessionOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets an override for all devices, a device group, or one device serial.</summary>
    Task SetDeviceOverrideAsync<T>(string path, T value, DeviceOverrideTarget target, CancellationToken cancellationToken = default);

    /// <summary>Removes an override for all devices, a device group, or one device serial.</summary>
    Task ResetDeviceOverrideAsync(string path, DeviceOverrideTarget target, CancellationToken cancellationToken = default);

    /// <summary>Gets a token that signals the publication of a new snapshot.</summary>
    ConfigChangeToken GetChangeToken();

    /// <summary>Gets a typed effective value.</summary>
    T? GetValue<T>(string path);

    /// <summary>Gets a string effective value.</summary>
    string? GetString(string path);

    /// <summary>
    /// Raised after a value is set or reset. The event fires synchronously before the
    /// writing call returns, so propagation stays within the 100 ms budget; bulk layer
    /// swaps (project context, session clears, run configurations) publish a new
    /// snapshot and notify <see cref="GetChangeToken"/> without per-path events.
    /// </summary>
    event EventHandler<ConfigChangedEventArgs>? Changed;
}
