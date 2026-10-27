namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// The entry point implemented by every plugin.
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// Initializes the plugin and registers its extensions.
    /// </summary>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Releases resources and event subscriptions.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Metadata about a loaded or installed plugin.
/// </summary>
public interface IPluginInfo
{
    /// <summary>Gets the unique plugin identifier.</summary>
    string Id { get; }

    /// <summary>Gets the display name.</summary>
    string Name { get; }

    /// <summary>Gets the plugin version.</summary>
    string Version { get; }

    /// <summary>Gets the target API version.</summary>
    string ApiVersion { get; }

    /// <summary>Gets the plugin installation directory.</summary>
    string InstallPath { get; }
}

/// <summary>
/// Services and metadata provided to a plugin while it is loaded.
/// </summary>
public interface IPluginContext
{
    /// <summary>Gets metadata about the plugin.</summary>
    IPluginInfo Info { get; }

    /// <summary>Gets the plugin-scoped logger.</summary>
    Microsoft.Extensions.Logging.ILogger Logger { get; }

    /// <summary>Gets the extension registry shared with built-in features.</summary>
    IPluginRegistry Extensions { get; }

    /// <summary>Gets the plugin's private settings store.</summary>
    IPluginSettings Settings { get; }

    /// <summary>Gets the plugin's private data directory.</summary>
    string DataDirectory { get; }
}
