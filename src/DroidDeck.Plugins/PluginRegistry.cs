using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Rendering;

namespace DroidDeck.Plugins;

/// <summary>
/// The host-wide extension registry. Built-ins register directly; each plugin
/// receives a scope that attributes its contributions to its plugin id.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry, IRendererRegistrar
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
    private readonly List<RendererRegistration> _renderers = [];
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
    public IReadOnlyList<RendererRegistration> Renderers
    {
        get
        {
            lock (_sync)
            {
                return _renderers.ToArray();
            }
        }
    }

    /// <summary>Creates a registry view that attributes additions to a plugin.</summary>
    public IPluginRegistry CreateScope(string pluginId)
    {
        ValidatePluginId(pluginId);
        return new ScopedPluginRegistry(this, pluginId);
    }

    /// <inheritdoc />
    public void AddRenderer(Type rendererType) => AddRenderer(PluginConstants.BuiltInPluginId, rendererType);

    /// <inheritdoc />
    public void AddCommand(ICommand command) => AddCommand(PluginConstants.BuiltInPluginId, command);

    /// <inheritdoc />
    public void AddProjectDetector(IProjectDetector detector)
        => AddProjectDetector(PluginConstants.BuiltInPluginId, detector);

    /// <inheritdoc />
    public void AddDeviceAction(IDeviceAction action)
        => AddDeviceAction(PluginConstants.BuiltInPluginId, action);

    /// <inheritdoc />
    public void AddDeviceTab(IDeviceTabProvider tab)
        => AddDeviceTab(PluginConstants.BuiltInPluginId, tab);

    /// <inheritdoc />
    public void AddToolbarAction(IDeviceToolbarAction action)
        => AddToolbarAction(PluginConstants.BuiltInPluginId, action);

    /// <inheritdoc />
    public void AddRunStep(IRunStep step) => AddRunStep(PluginConstants.BuiltInPluginId, step);

    /// <inheritdoc />
    public void AddLogParser(ILogParser parser) => AddLogParser(PluginConstants.BuiltInPluginId, parser);

    /// <inheritdoc />
    public void AddHighlightRuleProvider(IHighlightRuleProvider provider) =>
        AddHighlightRuleProvider(PluginConstants.BuiltInPluginId, provider);

    /// <inheritdoc />
    public void AddTestRunner(ITestRunner runner) => AddTestRunner(PluginConstants.BuiltInPluginId, runner);

    /// <inheritdoc />
    public void AddNavigationPage(INavigationPage page)
        => AddNavigationPage(PluginConstants.BuiltInPluginId, page);

    /// <inheritdoc />
    public void AddProjectTab(IProjectTab tab) => AddProjectTab(PluginConstants.BuiltInPluginId, tab);

    /// <inheritdoc />
    public void AddHomeWidget(IHomeWidget widget) => AddHomeWidget(PluginConstants.BuiltInPluginId, widget);

    /// <inheritdoc />
    public void AddSessionPanel(ISessionPanel panel) => AddSessionPanel(PluginConstants.BuiltInPluginId, panel);

    /// <inheritdoc />
    public void AddSettingsSection(ISettingsSection section) =>
        AddSettingsSection(PluginConstants.BuiltInPluginId, section);

    /// <inheritdoc />
    public void AddStatusBarItem(IStatusBarItem item) => AddStatusBarItem(PluginConstants.BuiltInPluginId, item);

    /// <inheritdoc />
    public void AddDialog(IPluginDialog dialog) => AddDialog(PluginConstants.BuiltInPluginId, dialog);

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
            removed += _renderers.RemoveAll(registration => registration.PluginId == pluginId);
            RebuildOwners();
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    internal void AddCommand(string pluginId, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Add(pluginId, command, _commands);
    }

    internal void AddProjectDetector(string pluginId, IProjectDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        Add(pluginId, detector, _projectDetectors);
    }

    internal void AddDeviceAction(string pluginId, IDeviceAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Add(pluginId, action, _deviceActions);
    }

    internal void AddDeviceTab(string pluginId, IDeviceTabProvider tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Add(pluginId, tab, _deviceTabs);
    }

    internal void AddToolbarAction(string pluginId, IDeviceToolbarAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Add(pluginId, action, _toolbarActions);
    }

    internal void AddRunStep(string pluginId, IRunStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Add(pluginId, step, _runSteps);
    }

    internal void AddLogParser(string pluginId, ILogParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        Add(pluginId, parser, _logParsers);
    }

    internal void AddHighlightRuleProvider(string pluginId, IHighlightRuleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Add(pluginId, provider, _highlightRuleProviders);
    }

    internal void AddTestRunner(string pluginId, ITestRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        Add(pluginId, runner, _testRunners);
    }

    internal void AddNavigationPage(string pluginId, INavigationPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Add(pluginId, page, _navigationPages);
    }

    internal void AddProjectTab(string pluginId, IProjectTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        Add(pluginId, tab, _projectTabs);
    }

    internal void AddHomeWidget(string pluginId, IHomeWidget widget)
    {
        ArgumentNullException.ThrowIfNull(widget);
        Add(pluginId, widget, _homeWidgets);
    }

    internal void AddSessionPanel(string pluginId, ISessionPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        Add(pluginId, panel, _sessionPanels);
    }

    internal void AddSettingsSection(string pluginId, ISettingsSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        Add(pluginId, section, _settingsSections);
    }

    internal void AddStatusBarItem(string pluginId, IStatusBarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Add(pluginId, item, _statusBarItems);
    }

    internal void AddDialog(string pluginId, IPluginDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Add(pluginId, dialog, _dialogs);
    }

    internal void AddRenderer(string pluginId, Type rendererType)
    {
        ArgumentNullException.ThrowIfNull(rendererType);
        ValidatePluginId(pluginId);
        if (!typeof(IRenderer).IsAssignableFrom(rendererType) ||
            rendererType.IsAbstract ||
            rendererType.IsInterface ||
            rendererType.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ArgumentException(
                $"Renderer type {rendererType.FullName} must implement {nameof(IRenderer)} and have a public parameterless constructor.",
                nameof(rendererType));
        }

        lock (_sync)
        {
            _renderers.Add(new RendererRegistration(pluginId, rendererType));
            _owners[rendererType] = pluginId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add<T>(string pluginId, T value, List<PluginExtension<T>> target)
        where T : class
    {
        ValidatePluginId(pluginId);
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

        foreach (var registration in _renderers)
        {
            _owners[registration.RendererType] = registration.PluginId;
        }
    }

    /// <summary>Finds a plugin's dialog by its stable identifier.</summary>
    public PluginExtension<IPluginDialog>? FindDialog(string pluginId, string dialogId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogId);
        return Dialogs.FirstOrDefault(extension =>
            string.Equals(extension.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(extension.Value.Id, dialogId, StringComparison.Ordinal));
    }

    private PluginExtension<T>[] Snapshot<T>(List<PluginExtension<T>> source)
        where T : class
    {
        lock (_sync)
        {
            return source.ToArray();
        }
    }

    private static void ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
    }

    private sealed class ScopedPluginRegistry(PluginRegistry root, string pluginId) : IPluginRegistry, IRendererRegistrar
    {
        public IReadOnlyList<PluginExtension<ICommand>> Commands => root.Commands;

        public IReadOnlyList<PluginExtension<IProjectDetector>> ProjectDetectors => root.ProjectDetectors;

        public IReadOnlyList<PluginExtension<IDeviceAction>> DeviceActions => root.DeviceActions;

        public IReadOnlyList<PluginExtension<IDeviceTabProvider>> DeviceTabs => root.DeviceTabs;

        public IReadOnlyList<PluginExtension<IDeviceToolbarAction>> ToolbarActions => root.ToolbarActions;

        public IReadOnlyList<PluginExtension<IRunStep>> RunSteps => root.RunSteps;

        public IReadOnlyList<PluginExtension<ILogParser>> LogParsers => root.LogParsers;

        public IReadOnlyList<PluginExtension<IHighlightRuleProvider>> HighlightRuleProviders =>
            root.HighlightRuleProviders;

        public IReadOnlyList<PluginExtension<ITestRunner>> TestRunners => root.TestRunners;

        public IReadOnlyList<PluginExtension<INavigationPage>> NavigationPages => root.NavigationPages;

        public IReadOnlyList<PluginExtension<IProjectTab>> ProjectTabs => root.ProjectTabs;

        public IReadOnlyList<PluginExtension<IHomeWidget>> HomeWidgets => root.HomeWidgets;

        public IReadOnlyList<PluginExtension<ISessionPanel>> SessionPanels => root.SessionPanels;

        public IReadOnlyList<PluginExtension<ISettingsSection>> SettingsSections => root.SettingsSections;

        public IReadOnlyList<PluginExtension<IStatusBarItem>> StatusBarItems => root.StatusBarItems;

        public IReadOnlyList<PluginExtension<IPluginDialog>> Dialogs => root.Dialogs;

        public IReadOnlyList<RendererRegistration> Renderers => root.Renderers;

        public event EventHandler? Changed
        {
            add => root.Changed += value;
            remove => root.Changed -= value;
        }

        public void AddCommand(ICommand command) => root.AddCommand(pluginId, command);

        public void AddProjectDetector(IProjectDetector detector) => root.AddProjectDetector(pluginId, detector);

        public void AddDeviceAction(IDeviceAction action) => root.AddDeviceAction(pluginId, action);

        public void AddDeviceTab(IDeviceTabProvider tab) => root.AddDeviceTab(pluginId, tab);

        public void AddToolbarAction(IDeviceToolbarAction action) => root.AddToolbarAction(pluginId, action);

        public void AddRunStep(IRunStep step) => root.AddRunStep(pluginId, step);

        public void AddLogParser(ILogParser parser) => root.AddLogParser(pluginId, parser);

        public void AddHighlightRuleProvider(IHighlightRuleProvider provider) =>
            root.AddHighlightRuleProvider(pluginId, provider);

        public void AddTestRunner(ITestRunner runner) => root.AddTestRunner(pluginId, runner);

        public void AddNavigationPage(INavigationPage page) => root.AddNavigationPage(pluginId, page);

        public void AddProjectTab(IProjectTab tab) => root.AddProjectTab(pluginId, tab);

        public void AddHomeWidget(IHomeWidget widget) => root.AddHomeWidget(pluginId, widget);

        public void AddSessionPanel(ISessionPanel panel) => root.AddSessionPanel(pluginId, panel);

        public void AddSettingsSection(ISettingsSection section) => root.AddSettingsSection(pluginId, section);

        public void AddStatusBarItem(IStatusBarItem item) => root.AddStatusBarItem(pluginId, item);

        public void AddDialog(IPluginDialog dialog) => root.AddDialog(pluginId, dialog);

        public void AddRenderer(Type rendererType) => root.AddRenderer(pluginId, rendererType);

        public string? GetOwner(Type extensionType) => root.GetOwner(extensionType);

        public int RemoveAll(string targetPluginId) => root.RemoveAll(targetPluginId);
    }
}
