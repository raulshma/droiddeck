namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// A page contributed to the shell navigation. The host creates the view only
/// when the user opens the page; the returned content may be a host UI element,
/// typically composed from the component kit in <c>DroidDeck.Plugin.UI</c>.
/// The host owns the result and keeps it inside a fault boundary.
/// </summary>
public interface INavigationPage
{
    /// <summary>Gets the stable page identifier, unique across plugins.</summary>
    string Id { get; }

    /// <summary>Gets the title shown in navigation and in the page header.</summary>
    string Title { get; }

    /// <summary>Gets a Segoe UI glyph for the navigation entry, or empty for the default icon.</summary>
    string Icon { get; }

    /// <summary>Creates the page content. The host owns the result.</summary>
    Task<object> CreateViewAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A tab contributed to the projects page. The host creates the view lazily
/// when the tab is first shown and keeps it inside a fault boundary.
/// </summary>
public interface IProjectTab
{
    /// <summary>Gets the stable tab identifier.</summary>
    string Id { get; }

    /// <summary>Gets the tab title.</summary>
    string Title { get; }

    /// <summary>Gets the ordering weight; lower values appear first.</summary>
    int Order { get; }

    /// <summary>Creates the tab content. The host owns the result.</summary>
    Task<object> CreateViewAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A widget contributed to the home page. The host creates the view after the
/// first frame, one widget at a time, so a slow or failing widget can neither
/// block Home nor its siblings.
/// </summary>
public interface IHomeWidget
{
    /// <summary>Gets the stable widget identifier.</summary>
    string Id { get; }

    /// <summary>Gets the widget title.</summary>
    string Title { get; }

    /// <summary>Gets the ordering weight; lower values appear first.</summary>
    int Order { get; }

    /// <summary>Creates the widget content. The host owns the result.</summary>
    Task<object> CreateViewAsync(CancellationToken cancellationToken);
}

/// <summary>Registration helpers for the UI extension points.</summary>
public static class PluginUiRegistryExtensions
{
    /// <summary>Registers a navigation page the host creates when the user opens it.</summary>
    public static void AddNavigationPage<TPage>(this IPluginRegistry registry)
        where TPage : INavigationPage, new() => registry.AddNavigationPage(new TPage());

    /// <summary>Registers a project tab the host creates when the tab is first shown.</summary>
    public static void AddProjectTab<TTab>(this IPluginRegistry registry)
        where TTab : IProjectTab, new() => registry.AddProjectTab(new TTab());

    /// <summary>Registers a home widget using the order the widget declares.</summary>
    public static void AddHomeWidget<TWidget>(this IPluginRegistry registry)
        where TWidget : IHomeWidget, new() => registry.AddHomeWidget(new TWidget());

    /// <summary>Registers a home widget whose order overrides the widget's declared value.</summary>
    public static void AddHomeWidget<TWidget>(this IPluginRegistry registry, int order)
        where TWidget : IHomeWidget, new() => registry.AddHomeWidget(new OrderedHomeWidget<TWidget>(new TWidget(), order));
}

/// <summary>Adapts a home widget so the order supplied at registration wins.</summary>
/// <typeparam name="TWidget">The widget type.</typeparam>
/// <param name="inner">The wrapped widget.</param>
/// <param name="order">The ordering weight.</param>
file sealed class OrderedHomeWidget<TWidget>(TWidget inner, int order) : IHomeWidget
    where TWidget : IHomeWidget
{
    /// <inheritdoc />
    public string Id => inner.Id;

    /// <inheritdoc />
    public string Title => inner.Title;

    /// <inheritdoc />
    public int Order => order;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken) =>
        inner.CreateViewAsync(cancellationToken);
}
