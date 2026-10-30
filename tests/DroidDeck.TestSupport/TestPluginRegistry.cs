using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.TestSupport;

/// <summary>
/// An in-memory extension registry for tests that exercise host pipelines without
/// loading the plugin host.
/// </summary>
public sealed class TestPluginRegistry : IPluginRegistry
{
    private readonly List<PluginExtension<ICommand>> _commands = [];
    private readonly List<PluginExtension<IProjectDetector>> _projectDetectors = [];
    private readonly List<PluginExtension<IDeviceAction>> _deviceActions = [];
    private readonly List<PluginExtension<IDeviceTabProvider>> _deviceTabs = [];
    private readonly List<PluginExtension<IDeviceToolbarAction>> _toolbarActions = [];
    private readonly List<PluginExtension<IRunStep>> _runSteps = [];
    private readonly List<PluginExtension<ILogParser>> _logParsers = [];
    private readonly List<PluginExtension<IHighlightRuleProvider>> _highlightRuleProviders = [];
    private readonly List<PluginExtension<ITestRunner>> _testRunners = [];
    private readonly List<PluginExtension<INavigationPage>> _navigationPages = [];
    private readonly List<PluginExtension<IProjectTab>> _projectTabs = [];
    private readonly List<PluginExtension<IHomeWidget>> _homeWidgets = [];
    private readonly List<PluginExtension<ISessionPanel>> _sessionPanels = [];
    private readonly List<PluginExtension<ISettingsSection>> _settingsSections = [];
    private readonly List<PluginExtension<IStatusBarItem>> _statusBarItems = [];
    private readonly List<PluginExtension<IPluginDialog>> _dialogs = [];
    private readonly Dictionary<Type, string> _owners = [];
    private readonly object _sync = new();

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<ICommand>> Commands => Snapshot(_commands);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IProjectDetector>> ProjectDetectors => Snapshot(_projectDetectors);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IDeviceAction>> DeviceActions => Snapshot(_deviceActions);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IDeviceTabProvider>> DeviceTabs => Snapshot(_deviceTabs);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IDeviceToolbarAction>> ToolbarActions => Snapshot(_toolbarActions);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IRunStep>> RunSteps => Snapshot(_runSteps);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<ILogParser>> LogParsers => Snapshot(_logParsers);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IHighlightRuleProvider>> HighlightRuleProviders =>
        Snapshot(_highlightRuleProviders);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<ITestRunner>> TestRunners => Snapshot(_testRunners);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<INavigationPage>> NavigationPages => Snapshot(_navigationPages);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IProjectTab>> ProjectTabs => Snapshot(_projectTabs);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IHomeWidget>> HomeWidgets => Snapshot(_homeWidgets);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<ISessionPanel>> SessionPanels => Snapshot(_sessionPanels);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<ISettingsSection>> SettingsSections => Snapshot(_settingsSections);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IStatusBarItem>> StatusBarItems => Snapshot(_statusBarItems);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IPluginDialog>> Dialogs => Snapshot(_dialogs);

    /// <inheritdoc />
    public void AddCommand(ICommand command) => Add(PluginConstants.BuiltInPluginId, command, _commands);

    /// <inheritdoc />
    public void AddProjectDetector(IProjectDetector detector) =>
        Add(PluginConstants.BuiltInPluginId, detector, _projectDetectors);

    /// <inheritdoc />
    public void AddDeviceAction(IDeviceAction action) => Add(PluginConstants.BuiltInPluginId, action, _deviceActions);

    /// <inheritdoc />
    public void AddDeviceTab(IDeviceTabProvider tab) => Add(PluginConstants.BuiltInPluginId, tab, _deviceTabs);

    /// <inheritdoc />
    public void AddToolbarAction(IDeviceToolbarAction action) =>
        Add(PluginConstants.BuiltInPluginId, action, _toolbarActions);

    /// <inheritdoc />
    public void AddRunStep(IRunStep step) => Add(PluginConstants.BuiltInPluginId, step, _runSteps);

    /// <inheritdoc />
    public void AddLogParser(ILogParser parser) => Add(PluginConstants.BuiltInPluginId, parser, _logParsers);

    /// <inheritdoc />
    public void AddHighlightRuleProvider(IHighlightRuleProvider provider) =>
        Add(PluginConstants.BuiltInPluginId, provider, _highlightRuleProviders);

    /// <inheritdoc />
    public void AddTestRunner(ITestRunner runner) => Add(PluginConstants.BuiltInPluginId, runner, _testRunners);

    /// <inheritdoc />
    public void AddNavigationPage(INavigationPage page) =>
        Add(PluginConstants.BuiltInPluginId, page, _navigationPages);

    /// <inheritdoc />
    public void AddProjectTab(IProjectTab tab) => Add(PluginConstants.BuiltInPluginId, tab, _projectTabs);

    /// <inheritdoc />
    public void AddHomeWidget(IHomeWidget widget) => Add(PluginConstants.BuiltInPluginId, widget, _homeWidgets);

    /// <inheritdoc />
    public void AddSessionPanel(ISessionPanel panel) => Add(PluginConstants.BuiltInPluginId, panel, _sessionPanels);

    /// <inheritdoc />
    public void AddSettingsSection(ISettingsSection section) =>
        Add(PluginConstants.BuiltInPluginId, section, _settingsSections);

    /// <inheritdoc />
    public void AddStatusBarItem(IStatusBarItem item) => Add(PluginConstants.BuiltInPluginId, item, _statusBarItems);

    /// <inheritdoc />
    public void AddDialog(IPluginDialog dialog) => Add(PluginConstants.BuiltInPluginId, dialog, _dialogs);

    /// <inheritdoc />
    public string? GetOwner(Type extensionType)
    {
        ArgumentNullException.ThrowIfNull(extensionType);
        lock (_sync)
        {
            return _owners.TryGetValue(extensionType, out var owner) ? owner : null;
        }
    }

    /// <inheritdoc />
    public int RemoveAll(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        int removed;
        lock (_sync)
        {
            removed = _commands.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _projectDetectors.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _deviceActions.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _deviceTabs.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _toolbarActions.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _runSteps.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _logParsers.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _highlightRuleProviders.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _testRunners.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _navigationPages.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _projectTabs.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _homeWidgets.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _sessionPanels.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _settingsSections.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _statusBarItems.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _dialogs.RemoveAll(extension => extension.PluginId == pluginId);
            RebuildOwners();
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private void Add<T>(string pluginId, T value, List<PluginExtension<T>> target)
        where T : class
    {
        lock (_sync)
        {
            target.Add(new PluginExtension<T>(pluginId, value));
            _owners[value.GetType()] = pluginId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildOwners()
    {
        _owners.Clear();
        foreach (var extension in _commands)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _projectDetectors)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _deviceActions)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _deviceTabs)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _toolbarActions)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _runSteps)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _logParsers)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _highlightRuleProviders)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _testRunners)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _navigationPages)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _projectTabs)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _homeWidgets)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _sessionPanels)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _settingsSections)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _statusBarItems)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _dialogs)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }
    }

    private PluginExtension<T>[] Snapshot<T>(List<PluginExtension<T>> source)
        where T : class
    {
        lock (_sync)
        {
            return source.ToArray();
        }
    }
}
