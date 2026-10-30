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

    /// <summary>Registers a log parser.</summary>
    void AddLogParser(ILogParser parser);

    /// <summary>Registers a highlight rule provider.</summary>
    void AddHighlightRuleProvider(IHighlightRuleProvider provider);

    /// <summary>Registers a test runner.</summary>
    void AddTestRunner(ITestRunner runner);

    /// <summary>Registers a navigation page.</summary>
    void AddNavigationPage(INavigationPage page);

    /// <summary>Registers a project tab.</summary>
    void AddProjectTab(IProjectTab tab);

    /// <summary>Registers a home widget.</summary>
    void AddHomeWidget(IHomeWidget widget);

    /// <summary>Registers a session panel.</summary>
    void AddSessionPanel(ISessionPanel panel);

    /// <summary>Registers a settings section.</summary>
    void AddSettingsSection(ISettingsSection section);

    /// <summary>Registers a status bar item.</summary>
    void AddStatusBarItem(IStatusBarItem item);

    /// <summary>Registers a dialog the plugin shows through <see cref="IPluginDialogService"/>.</summary>
    void AddDialog(IPluginDialog dialog);

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

    /// <summary>Gets all registered log parsers.</summary>
    IReadOnlyList<PluginExtension<ILogParser>> LogParsers { get; }

    /// <summary>Gets all registered highlight rule providers.</summary>
    IReadOnlyList<PluginExtension<IHighlightRuleProvider>> HighlightRuleProviders { get; }

    /// <summary>Gets all registered test runners.</summary>
    IReadOnlyList<PluginExtension<ITestRunner>> TestRunners { get; }

    /// <summary>Gets all registered navigation pages.</summary>
    IReadOnlyList<PluginExtension<INavigationPage>> NavigationPages { get; }

    /// <summary>Gets all registered project tabs.</summary>
    IReadOnlyList<PluginExtension<IProjectTab>> ProjectTabs { get; }

    /// <summary>Gets all registered home widgets.</summary>
    IReadOnlyList<PluginExtension<IHomeWidget>> HomeWidgets { get; }

    /// <summary>Gets all registered session panels.</summary>
    IReadOnlyList<PluginExtension<ISessionPanel>> SessionPanels { get; }

    /// <summary>Gets all registered settings sections.</summary>
    IReadOnlyList<PluginExtension<ISettingsSection>> SettingsSections { get; }

    /// <summary>Gets all registered status bar items.</summary>
    IReadOnlyList<PluginExtension<IStatusBarItem>> StatusBarItems { get; }

    /// <summary>Gets all registered dialogs.</summary>
    IReadOnlyList<PluginExtension<IPluginDialog>> Dialogs { get; }

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
