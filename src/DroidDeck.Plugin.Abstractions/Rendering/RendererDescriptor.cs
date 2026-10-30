namespace DroidDeck.Plugin.Abstractions.Rendering;

/// <summary>How a renderer is behaving after repeated use.</summary>
public enum RendererHealth
{
    /// <summary>The renderer has not been used yet.</summary>
    Unknown,

    /// <summary>The renderer is working.</summary>
    Healthy,

    /// <summary>The renderer failed at least once but is still selectable.</summary>
    Failing,

    /// <summary>The renderer failed repeatedly and is skipped until it is re-enabled.</summary>
    Disabled,
}

/// <summary>
/// Identifies a renderer and the plugin that contributed it. The descriptor is
/// safe to hold outside the UI; the view is created from the renderer on demand.
/// </summary>
/// <param name="Id">The stable renderer identifier.</param>
/// <param name="PluginId">The contributing plugin, or the built-in identifier.</param>
/// <param name="Kind">The surface the renderer replaces.</param>
/// <param name="DisplayName">The name shown in View as menus.</param>
/// <param name="Priority">The ordering weight; higher values are preferred.</param>
/// <param name="IsBuiltIn">Whether the host ships this renderer.</param>
public sealed record RendererDescriptor(
    string Id,
    string PluginId,
    RendererKind Kind,
    string DisplayName,
    int Priority = 0,
    bool IsBuiltIn = false);

/// <summary>A renderer descriptor together with its current health and availability.</summary>
public sealed record RendererStatus(RendererDescriptor Renderer)
{
    /// <summary>Gets whether the user (or the failure guard) allows this renderer.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets the current health.</summary>
    public required RendererHealth Health { get; init; }

    /// <summary>Gets the number of consecutive failures.</summary>
    public required int FailureCount { get; init; }

    /// <summary>Gets why the renderer was disabled, when it was.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>Gets the renderer identifier.</summary>
    public string Id => Renderer.Id;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => Renderer.DisplayName;

    /// <summary>Gets the renderer kind.</summary>
    public RendererKind Kind => Renderer.Kind;

    /// <summary>Gets whether the renderer can currently be selected.</summary>
    public bool IsAvailable => Enabled && Health != RendererHealth.Disabled;
}

/// <summary>
/// The result of asking a renderer to create a view. A fallback result carries the
/// error that made the preferred renderer fail.
/// </summary>
/// <param name="Renderer">The renderer that produced the view, when one did.</param>
/// <param name="View">The created view, or <see langword="null"/> when creation failed.</param>
/// <param name="IsFallback">Whether the preferred renderer failed and the built-in was used.</param>
/// <param name="Error">The error that caused the fallback, when there was one.</param>
public sealed record RendererRunResult(
    RendererDescriptor? Renderer,
    object? View,
    bool IsFallback,
    Exception? Error)
{
    /// <summary>Gets a result for a kind with no renderers.</summary>
    public static RendererRunResult Empty { get; } = new(null, null, false, null);
}
