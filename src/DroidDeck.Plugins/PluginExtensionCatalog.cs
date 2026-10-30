using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>A device action with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Action">The action.</param>
public sealed record DeviceActionEntry(string PluginId, IDeviceAction Action)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

/// <summary>A toolbar action with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Action">The action.</param>
public sealed record ToolbarActionEntry(string PluginId, IDeviceToolbarAction Action)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

/// <summary>A device tab with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Tab">The tab provider.</param>
public sealed record DeviceTabEntry(string PluginId, IDeviceTabProvider Tab)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

/// <summary>A navigation page with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Page">The navigation page.</param>
public sealed record NavigationPageEntry(string PluginId, INavigationPage Page)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

/// <summary>A project tab with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Tab">The project tab.</param>
public sealed record ProjectTabEntry(string PluginId, IProjectTab Tab)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

/// <summary>A home widget with its contributing source.</summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Widget">The home widget.</param>
public sealed record HomeWidgetEntry(string PluginId, IHomeWidget Widget)
{
    /// <summary>Gets the source label shown in the UI.</summary>
    public string SourceLabel => PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : PluginId;
}

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
    public IReadOnlyList<DeviceActionEntry> DeviceActions => _registry.DeviceActions
        .OrderBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static extension => new DeviceActionEntry(extension.PluginId, extension.Value))
        .ToArray();

    /// <summary>Gets toolbar actions ordered by weight, source, and title.</summary>
    public IReadOnlyList<ToolbarActionEntry> ToolbarActions => _registry.ToolbarActions
        .OrderBy(static extension => extension.Value.Order)
        .ThenBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static extension => new ToolbarActionEntry(extension.PluginId, extension.Value))
        .ToArray();

    /// <summary>Gets device tabs ordered by weight, source, and title.</summary>
    public IReadOnlyList<DeviceTabEntry> DeviceTabs => _registry.DeviceTabs
        .OrderBy(static extension => extension.Value.Order)
        .ThenBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static extension => new DeviceTabEntry(extension.PluginId, extension.Value))
        .ToArray();

    /// <summary>Gets navigation pages ordered by source, title, and identifier.</summary>
    public IReadOnlyList<NavigationPageEntry> NavigationPages => _registry.NavigationPages
        .OrderBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static extension => new NavigationPageEntry(extension.PluginId, extension.Value))
        .ToArray();

    /// <summary>Gets project tabs ordered by weight, source, and title.</summary>
    public IReadOnlyList<ProjectTabEntry> ProjectTabs => _registry.ProjectTabs
        .OrderBy(static extension => extension.Value.Order)
        .ThenBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static extension => new ProjectTabEntry(extension.PluginId, extension.Value))
        .ToArray();

    /// <summary>Gets home widgets ordered by weight, source, and title.</summary>
    public IReadOnlyList<HomeWidgetEntry> HomeWidgets => _registry.HomeWidgets
        .OrderBy(static extension => extension.Value.Order)
        .ThenBy(static extension => extension.PluginId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Title, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static extension => extension.Value.Id, StringComparer.OrdinalIgnoreCase)
        .Select(static extension => new HomeWidgetEntry(extension.PluginId, extension.Value))
        .ToArray();
}
