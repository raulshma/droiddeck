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

/// <summary>
/// Static information about the session a panel attaches to, supplied once
/// when the host creates the panel view.
/// </summary>
/// <param name="SessionId">The run session identifier, when the panel attaches to a run.</param>
/// <param name="DeviceSerial">The device the session runs against, when known.</param>
/// <param name="DeviceName">The device display name, when known.</param>
public sealed record SessionPanelContext(string? SessionId, string? DeviceSerial, string? DeviceName);

/// <summary>
/// A point-in-time view of the live session the host delivers to session
/// panels. Recent log lines are pre-formatted, so panels stay UI-free and
/// never touch host log models.
/// </summary>
/// <param name="SessionId">The run session identifier, when the panel attaches to a run.</param>
/// <param name="DeviceSerial">The device the session runs against, when known.</param>
/// <param name="DeviceName">The device display name, when known.</param>
/// <param name="State">The session state name, for example "Running" or "Succeeded".</param>
/// <param name="IsFinished">Whether the session reached a terminal state.</param>
/// <param name="TotalLogLines">How many log lines the session produced so far.</param>
/// <param name="RecentLogLines">The most recent log lines, oldest first.</param>
public sealed record SessionPanelSnapshot(
    string? SessionId,
    string? DeviceSerial,
    string? DeviceName,
    string State,
    bool IsFinished,
    int TotalLogLines,
    IReadOnlyList<string> RecentLogLines)
{
    /// <summary>Creates an empty snapshot for the host's "no session" state.</summary>
    public static SessionPanelSnapshot Empty { get; } =
        new(null, null, null, "Idle", true, 0, Array.Empty<string>());
}

/// <summary>
/// A panel contributed to the session page. The host creates the view when a
/// session is shown and delivers <see cref="SessionPanelSnapshot"/> updates as
/// the session state and its logs advance. A panel whose update throws is
/// swapped for a fallback card without disturbing its siblings.
/// </summary>
public interface ISessionPanel
{
    /// <summary>Gets the stable panel identifier.</summary>
    string Id { get; }

    /// <summary>Gets the panel title.</summary>
    string Title { get; }

    /// <summary>Gets the ordering weight; lower values appear first.</summary>
    int Order { get; }

    /// <summary>Creates the panel content. The host owns the result.</summary>
    Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken);

    /// <summary>Applies a session update. Called on the UI thread; must not throw.</summary>
    void UpdateSession(SessionPanelSnapshot snapshot);
}

/// <summary>One field-level error produced by the host settings validator.</summary>
/// <param name="Field">The settings field the error belongs to.</param>
/// <param name="Message">The human-readable error text.</param>
public sealed record SettingsSectionError(string Field, string Message);

/// <summary>
/// The host services a settings section uses to read and write the plugin's
/// settings. Writes go through the host store, so values are validated against
/// the plugin's settings schema before they are persisted.
/// </summary>
public interface ISettingsSectionContext
{
    /// <summary>Gets the plugin's host-managed settings store.</summary>
    IPluginSettings Settings { get; }

    /// <summary>
    /// Validates the currently stored values through the host validator and
    /// returns one error per invalid field.
    /// </summary>
    Task<IReadOnlyList<SettingsSectionError>> ValidateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A section contributed to the settings page. The host creates the view inside
/// a host card and keeps it inside a fault boundary; the section persists its
/// values through the supplied <see cref="ISettingsSectionContext"/> so the host
/// validates every write.
/// </summary>
public interface ISettingsSection
{
    /// <summary>Gets the stable section identifier.</summary>
    string Id { get; }

    /// <summary>Gets the section title.</summary>
    string Title { get; }

    /// <summary>Gets an optional description shown under the title.</summary>
    string? Description { get; }

    /// <summary>Gets the ordering weight; lower values appear first.</summary>
    int Order { get; }

    /// <summary>Creates the section content. The host owns the result.</summary>
    Task<object> CreateViewAsync(ISettingsSectionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A text entry contributed to the window status bar. The host polls
/// <see cref="GetText"/> on the UI thread a few times a second and isolates
/// failures; a throwing item shows placeholder text instead.
/// </summary>
public interface IStatusBarItem
{
    /// <summary>Gets the stable item identifier.</summary>
    string Id { get; }

    /// <summary>Gets the tooltip text.</summary>
    string Title { get; }

    /// <summary>Gets the ordering weight; lower values appear first.</summary>
    int Order { get; }

    /// <summary>Returns the current display text. Called on the UI thread.</summary>
    string GetText();
}

/// <summary>
/// A status bar item the user can click. The host renders it as a button and
/// invokes <see cref="InvokeClickAsync"/> with failure isolation.
/// </summary>
public interface IStatusBarClickableItem : IStatusBarItem
{
    /// <summary>Runs the click action.</summary>
    Task InvokeClickAsync(CancellationToken cancellationToken);
}

/// <summary>One button of a plugin dialog.</summary>
/// <param name="Id">The stable button identifier returned to <see cref="IPluginDialog.OnClosedAsync"/>.</param>
/// <param name="Text">The button label.</param>
/// <param name="IsPrimary">Whether the button is the dialog's default action.</param>
public sealed record PluginDialogButton(string Id, string Text, bool IsPrimary);

/// <summary>
/// A dialog a plugin contributes. The host shows the content in a ContentDialog
/// on the shell's UI thread; plugins never host their own windows. The content
/// is created with timeout isolation, and the user's choice is delivered
/// through <see cref="OnClosedAsync"/>.
/// </summary>
public interface IPluginDialog
{
    /// <summary>Gets the stable dialog identifier, unique across the plugin.</summary>
    string Id { get; }

    /// <summary>Gets the dialog title.</summary>
    string Title { get; }

    /// <summary>Gets the dialog buttons. An empty list means a single Close button.</summary>
    IReadOnlyList<PluginDialogButton> Buttons { get; }

    /// <summary>Creates the dialog content. The host owns the result.</summary>
    Task<object> CreateContentAsync(CancellationToken cancellationToken);

    /// <summary>Receives the button that closed the dialog, or <see langword="null"/> when it was dismissed.</summary>
    Task OnClosedAsync(string? buttonId, CancellationToken cancellationToken);
}

/// <summary>The outcome of showing a plugin dialog.</summary>
/// <param name="Shown">Whether the dialog was shown to the user.</param>
/// <param name="ButtonId">The button that closed the dialog, or <see langword="null"/> when dismissed.</param>
/// <param name="Error">Why the dialog could not be shown, when <paramref name="Shown"/> is <see langword="false"/>.</param>
public sealed record PluginDialogResult(bool Shown, string? ButtonId, string? Error)
{
    /// <summary>Creates the result of a dialog the user closed.</summary>
    public static PluginDialogResult Closed(string? buttonId) => new(true, buttonId, null);

    /// <summary>Creates the result of a dialog that could not be shown.</summary>
    public static PluginDialogResult Failed(string error) => new(false, null, error);
}

/// <summary>
/// The host service plugins use to show one of their registered dialogs. The
/// host presents the dialog on the shell's UI thread and isolates plugin
/// faults; a plugin never hosts its own window.
/// </summary>
public interface IPluginDialogService
{
    /// <summary>
    /// Shows this plugin's dialog registered under <paramref name="dialogId"/>
    /// and returns the user's choice.
    /// </summary>
    Task<PluginDialogResult> ShowAsync(string dialogId, CancellationToken cancellationToken = default);
}

/// <summary>Creates per-plugin dialog services. Host infrastructure only.</summary>
public interface IPluginDialogServiceProvider
{
    /// <summary>Creates a dialog service that only shows dialogs owned by <paramref name="pluginId"/>.</summary>
    IPluginDialogService CreateScope(string pluginId);
}

/// <summary>A dialog service for hosts that cannot show dialogs.</summary>
public sealed class NullPluginDialogService : IPluginDialogService
{
    /// <summary>Gets the shared instance.</summary>
    public static NullPluginDialogService Instance { get; } = new();

    private NullPluginDialogService()
    {
    }

    /// <inheritdoc />
    public Task<PluginDialogResult> ShowAsync(string dialogId, CancellationToken cancellationToken = default) =>
        Task.FromResult(PluginDialogResult.Failed("The host cannot show plugin dialogs."));
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

    /// <summary>Registers a session panel using the order the panel declares.</summary>
    public static void AddSessionPanel<TPanel>(this IPluginRegistry registry)
        where TPanel : ISessionPanel, new() => registry.AddSessionPanel(new TPanel());

    /// <summary>Registers a session panel whose order overrides the panel's declared value.</summary>
    public static void AddSessionPanel<TPanel>(this IPluginRegistry registry, int order)
        where TPanel : ISessionPanel, new() => registry.AddSessionPanel(new OrderedSessionPanel<TPanel>(new TPanel(), order));

    /// <summary>Registers a settings section using the order the section declares.</summary>
    public static void AddSettingsSection<TSection>(this IPluginRegistry registry)
        where TSection : ISettingsSection, new() => registry.AddSettingsSection(new TSection());

    /// <summary>Registers a settings section whose order overrides the section's declared value.</summary>
    public static void AddSettingsSection<TSection>(this IPluginRegistry registry, int order)
        where TSection : ISettingsSection, new() =>
        registry.AddSettingsSection(new OrderedSettingsSection<TSection>(new TSection(), order));

    /// <summary>Registers a status bar item using the order the item declares.</summary>
    public static void AddStatusBarItem<TItem>(this IPluginRegistry registry)
        where TItem : IStatusBarItem, new() => registry.AddStatusBarItem(new TItem());

    /// <summary>Registers a dialog the plugin shows through <see cref="IPluginDialogService"/>.</summary>
    public static void AddDialog<TDialog>(this IPluginRegistry registry)
        where TDialog : IPluginDialog, new() => registry.AddDialog(new TDialog());
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

/// <summary>Adapts a session panel so the order supplied at registration wins.</summary>
/// <typeparam name="TPanel">The panel type.</typeparam>
/// <param name="inner">The wrapped panel.</param>
/// <param name="order">The ordering weight.</param>
file sealed class OrderedSessionPanel<TPanel>(TPanel inner, int order) : ISessionPanel
    where TPanel : ISessionPanel
{
    /// <inheritdoc />
    public string Id => inner.Id;

    /// <inheritdoc />
    public string Title => inner.Title;

    /// <inheritdoc />
    public int Order => order;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken) =>
        inner.CreateViewAsync(context, cancellationToken);

    /// <inheritdoc />
    public void UpdateSession(SessionPanelSnapshot snapshot) => inner.UpdateSession(snapshot);
}

/// <summary>Adapts a settings section so the order supplied at registration wins.</summary>
/// <typeparam name="TSection">The section type.</typeparam>
/// <param name="inner">The wrapped section.</param>
/// <param name="order">The ordering weight.</param>
file sealed class OrderedSettingsSection<TSection>(TSection inner, int order) : ISettingsSection
    where TSection : ISettingsSection
{
    /// <inheritdoc />
    public string Id => inner.Id;

    /// <inheritdoc />
    public string Title => inner.Title;

    /// <inheritdoc />
    public string? Description => inner.Description;

    /// <inheritdoc />
    public int Order => order;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(ISettingsSectionContext context, CancellationToken cancellationToken) =>
        inner.CreateViewAsync(context, cancellationToken);
}
