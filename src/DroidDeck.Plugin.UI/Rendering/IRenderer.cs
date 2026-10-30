using DroidDeck.Plugin.Abstractions.Rendering;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>
/// A plugin or built-in view for a renderable surface. Instances are created on
/// demand by the host and disposed when the view closes.
/// </summary>
public interface IRenderer
{
    /// <summary>Gets the stable renderer identifier, unique across plugins.</summary>
    string Id { get; }

    /// <summary>Gets the name shown in View as menus.</summary>
    string DisplayName { get; }

    /// <summary>Gets the surface the renderer replaces.</summary>
    RendererKind Kind { get; }

    /// <summary>Gets the ordering weight among renderers of the same kind; higher wins.</summary>
    int Priority { get; }

    /// <summary>Gets whether the renderer can present the supplied data.</summary>
    bool CanRender(IRenderContext context);

    /// <summary>Creates the view. The host owns the returned element.</summary>
    FrameworkElement CreateView(IRenderContext context);

    /// <summary>Releases renderer resources when the view closes.</summary>
    void Dispose();
}

/// <summary>Services and data supplied when a renderer creates a view.</summary>
public interface IRenderContext
{
    /// <summary>Gets the stable data transfer object for the item being rendered.</summary>
    object Data { get; }

    /// <summary>Gets the surface being rendered.</summary>
    RendererKind Kind { get; }

    /// <summary>Gets the host service provider.</summary>
    IServiceProvider Services { get; }

    /// <summary>Gets the current theme information.</summary>
    IThemeService Theme { get; }

    /// <summary>Gets the UI dispatcher for async loading.</summary>
    IUiDispatcher Dispatcher { get; }

    /// <summary>Gets the token that is cancelled when the view closes.</summary>
    CancellationToken CancellationToken { get; }

    /// <summary>Gets the space available for the rendered view.</summary>
    Size AvailableSize { get; }
}

/// <summary>Theme facts a renderer can use without reaching into the shell.</summary>
public interface IThemeService
{
    /// <summary>Gets whether the dark theme is active.</summary>
    bool IsDark { get; }

    /// <summary>Gets whether a high-contrast theme is active.</summary>
    bool IsHighContrast { get; }
}

/// <summary>Marshals work to the UI thread.</summary>
public interface IUiDispatcher
{
    /// <summary>Gets whether the caller is already on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Runs an action on the UI thread.</summary>
    Task InvokeAsync(Action action);
}

/// <summary>The default render context handed to renderers.</summary>
public sealed class RendererContext(
    object data,
    RendererKind kind,
    IServiceProvider services,
    IThemeService theme,
    IUiDispatcher dispatcher,
    Size availableSize,
    CancellationToken cancellationToken) : IRenderContext
{
    /// <inheritdoc />
    public object Data { get; } = data ?? throw new ArgumentNullException(nameof(data));

    /// <inheritdoc />
    public RendererKind Kind { get; } = kind;

    /// <inheritdoc />
    public IServiceProvider Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc />
    public IThemeService Theme { get; } = theme ?? throw new ArgumentNullException(nameof(theme));

    /// <inheritdoc />
    public IUiDispatcher Dispatcher { get; } = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <inheritdoc />
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <inheritdoc />
    public Size AvailableSize { get; } = availableSize;
}
