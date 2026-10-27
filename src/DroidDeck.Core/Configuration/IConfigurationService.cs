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
    /// <summary>Gets the current immutable snapshot.</summary>
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

    /// <summary>Gets a typed effective value.</summary>
    T? GetValue<T>(string path);

    /// <summary>Gets a string effective value.</summary>
    string? GetString(string path);

    /// <summary>Raised after a value is set or reset.</summary>
    event EventHandler<ConfigChangedEventArgs>? Changed;
}
