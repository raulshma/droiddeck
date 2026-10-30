using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// The shared shape of every catalog entry: the contributing plugin id plus
/// the label the host shows for it.
/// </summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
public abstract record PluginSurfaceEntry(string PluginId)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

/// <summary>A device action with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Action">The action.</param>
public sealed record DeviceActionEntry(string PluginId, IDeviceAction Action) : PluginSurfaceEntry(PluginId);

/// <summary>A toolbar action with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Action">The action.</param>
public sealed record ToolbarActionEntry(string PluginId, IDeviceToolbarAction Action) : PluginSurfaceEntry(PluginId);

/// <summary>A device tab with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Tab">The tab provider.</param>
public sealed record DeviceTabEntry(string PluginId, IDeviceTabProvider Tab) : PluginSurfaceEntry(PluginId);

/// <summary>A navigation page with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Page">The navigation page.</param>
public sealed record NavigationPageEntry(string PluginId, INavigationPage Page) : PluginSurfaceEntry(PluginId);

/// <summary>A project tab with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Tab">The project tab.</param>
public sealed record ProjectTabEntry(string PluginId, IProjectTab Tab) : PluginSurfaceEntry(PluginId);

/// <summary>A home widget with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Widget">The home widget.</param>
public sealed record HomeWidgetEntry(string PluginId, IHomeWidget Widget) : PluginSurfaceEntry(PluginId);

/// <summary>A session panel with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Panel">The session panel.</param>
public sealed record SessionPanelEntry(string PluginId, ISessionPanel Panel) : PluginSurfaceEntry(PluginId);

/// <summary>A settings section with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Section">The settings section.</param>
public sealed record SettingsSectionEntry(string PluginId, ISettingsSection Section) : PluginSurfaceEntry(PluginId);

/// <summary>A status bar item with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Item">The status bar item.</param>
public sealed record StatusBarItemEntry(string PluginId, IStatusBarItem Item) : PluginSurfaceEntry(PluginId);

/// <summary>
/// Presents plugin extensions in a predictable order and keeps the plugin id
/// attached so the host can show where each extension came from.
/// </summary>
public sealed class PluginExtensionCatalog
{
    private readonly PluginRegistry _registry;

    /// <summary>Creates a catalog over a registry.</summary>
    public PluginExtensionCatalog(PluginRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>Gets device actions ordered by source and name.</summary>
    public IReadOnlyList<DeviceActionEntry> DeviceActions => Ordered(
        _registry.DeviceActions,
        static extension => new DeviceActionEntry(extension.PluginId, extension.Value),
        static value => value.Name,
        static value => value.Id,
        order: null);

    /// <summary>Gets toolbar actions ordered by weight, source, and title.</summary>
    public IReadOnlyList<ToolbarActionEntry> ToolbarActions => Ordered(
        _registry.ToolbarActions,
        static extension => new ToolbarActionEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        static value => value.Id,
        order: static value => value.Order);

    /// <summary>Gets device tabs ordered by weight, source, and title.</summary>
    public IReadOnlyList<DeviceTabEntry> DeviceTabs => Ordered(
        _registry.DeviceTabs,
        static extension => new DeviceTabEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        static value => value.Id,
        order: static value => value.Order);

    /// <summary>Gets navigation pages ordered by source, title, and identifier.</summary>
    public IReadOnlyList<NavigationPageEntry> NavigationPages => Ordered(
        _registry.NavigationPages,
        static extension => new NavigationPageEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        static value => value.Id,
        order: null);

    /// <summary>Gets project tabs ordered by weight, source, and title.</summary>
    public IReadOnlyList<ProjectTabEntry> ProjectTabs => Ordered(
        _registry.ProjectTabs,
        static extension => new ProjectTabEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        static value => value.Id,
        order: static value => value.Order);

    /// <summary>Gets home widgets ordered by weight, source, and title.</summary>
    public IReadOnlyList<HomeWidgetEntry> HomeWidgets => Ordered(
        _registry.HomeWidgets,
        static extension => new HomeWidgetEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        static value => value.Id,
        order: static value => value.Order);

    /// <summary>Gets session panels ordered by weight, source, and title.</summary>
    public IReadOnlyList<SessionPanelEntry> SessionPanels => Ordered(
        _registry.SessionPanels,
        static extension => new SessionPanelEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        id: null,
        order: static value => value.Order);

    /// <summary>Gets settings sections ordered by weight, source, and title.</summary>
    public IReadOnlyList<SettingsSectionEntry> SettingsSections => Ordered(
        _registry.SettingsSections,
        static extension => new SettingsSectionEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        id: null,
        order: static value => value.Order);

    /// <summary>Gets status bar items ordered by weight, source, and title.</summary>
    public IReadOnlyList<StatusBarItemEntry> StatusBarItems => Ordered(
        _registry.StatusBarItems,
        static extension => new StatusBarItemEntry(extension.PluginId, extension.Value),
        static value => value.Title,
        id: null,
        order: static value => value.Order);

    /// <summary>
    /// Runs the shared ordering pipeline for one surface: optional weight
    /// first, then source plugin id, then the surface's label, then its stable
    /// identifier, and finally projects each extension into its entry record.
    /// </summary>
    private static TEntry[] Ordered<T, TEntry>(
        IReadOnlyList<PluginExtension<T>> extensions,
        Func<PluginExtension<T>, TEntry> project,
        Func<T, string> label,
        Func<T, string>? id,
        Func<T, int>? order)
        where T : class
    {
        IOrderedEnumerable<PluginExtension<T>> ordered = order is not null
            ? extensions.OrderBy(extension => order(extension.Value))
                .ThenBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
            : extensions.OrderBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase);

        ordered = ordered.ThenBy(extension => label(extension.Value), StringComparer.OrdinalIgnoreCase);
        if (id is not null)
        {
            ordered = ordered.ThenBy(extension => id(extension.Value), StringComparer.OrdinalIgnoreCase);
        }

        return ordered.Select(project).ToArray();
    }
}
