namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// A registered extension and the plugin that contributed it.
/// </summary>
/// <typeparam name="T">The extension contract type.</typeparam>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Value">The extension instance.</param>
public sealed record PluginExtension<T>(string PluginId, T Value)
    where T : class;

/// <summary>
/// The registry built-in features and plugins use to contribute extensions.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Registers a command.</summary>
    void AddCommand(ICommand command);

    /// <summary>Registers a project detector.</summary>
    void AddProjectDetector(IProjectDetector detector);

    /// <summary>Registers a device action.</summary>
    void AddDeviceAction(IDeviceAction action);

    /// <summary>Registers a device detail tab.</summary>
    void AddDeviceTab(IDeviceTabProvider tab);

    /// <summary>Registers a toolbar action.</summary>
    void AddToolbarAction(IDeviceToolbarAction action);

    /// <summary>Registers a run step.</summary>
    void AddRunStep(IRunStep step);

    /// <summary>Gets all registered commands.</summary>
    IReadOnlyList<PluginExtension<ICommand>> Commands { get; }

    /// <summary>Gets all registered project detectors.</summary>
    IReadOnlyList<PluginExtension<IProjectDetector>> ProjectDetectors { get; }

    /// <summary>Gets all registered device actions.</summary>
    IReadOnlyList<PluginExtension<IDeviceAction>> DeviceActions { get; }

    /// <summary>Gets all registered device detail tabs.</summary>
    IReadOnlyList<PluginExtension<IDeviceTabProvider>> DeviceTabs { get; }

    /// <summary>Gets all registered toolbar actions.</summary>
    IReadOnlyList<PluginExtension<IDeviceToolbarAction>> ToolbarActions { get; }

    /// <summary>Gets all registered run steps.</summary>
    IReadOnlyList<PluginExtension<IRunStep>> RunSteps { get; }

    /// <summary>Gets the plugin identifier that owns an extension type.</summary>
    string? GetOwner(Type extensionType);

    /// <summary>Removes all extensions contributed by a plugin.</summary>
    /// <returns>The number of removed extensions.</returns>
    int RemoveAll(string pluginId);

    /// <summary>Raised after any extension is added or removed.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Well-known plugin identifiers.
/// </summary>
public static class PluginConstants
{
    /// <summary>The identifier used for host built-in extensions.</summary>
    public const string BuiltInPluginId = "droiddeck.builtin";
}
