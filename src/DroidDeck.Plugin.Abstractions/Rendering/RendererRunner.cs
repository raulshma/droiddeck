namespace DroidDeck.Plugin.Abstractions.Rendering;

/// <summary>
/// Runs a renderer and converts failures into a built-in fallback. The runner is
/// UI-free; the caller supplies the view factory, so the host can wrap renderer
/// creation (and its timing) without duplicating fallback logic.
/// </summary>
public static class RendererRunner
{
    /// <summary>
    /// Creates the selected renderer's view, falling back to the best built-in
    /// when the preferred renderer fails, returns no view, or is disabled.
    /// </summary>
    /// <param name="catalog">The renderer catalog.</param>
    /// <param name="kind">The surface being rendered.</param>
    /// <param name="preferredId">A one-off View as choice for this item.</param>
    /// <param name="canRender">A predicate that rejects renderers that do not fit the data.</param>
    /// <param name="create">Creates a view from a renderer, returning <see langword="null"/> on failure.</param>
    public static RendererRunResult Render(
        RendererCatalog catalog,
        RendererKind kind,
        string? preferredId,
        Func<RendererDescriptor, bool>? canRender,
        Func<RendererDescriptor, object?> create)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(create);

        var selected = catalog.Select(kind, preferredId, canRender);
        if (selected is null)
        {
            return RendererRunResult.Empty;
        }

        try
        {
            var view = create(selected.Renderer) ??
                throw new InvalidOperationException($"Renderer '{selected.Renderer.Id}' returned no view.");
            catalog.ReportSuccess(selected.Renderer.Id);
            return new RendererRunResult(selected.Renderer, view, false, null);
        }
        catch (Exception error)
        {
            catalog.ReportFailure(selected.Renderer.Id, error.Message);
            return CreateFallback(catalog, kind, selected.Renderer, canRender, create, error);
        }
    }

    private static RendererRunResult CreateFallback(
        RendererCatalog catalog,
        RendererKind kind,
        RendererDescriptor failed,
        Func<RendererDescriptor, bool>? canRender,
        Func<RendererDescriptor, object?> create,
        Exception error)
    {
        var fallback = catalog.SelectBuiltIn(kind, canRender, failed.Id);
        if (fallback is null)
        {
            return new RendererRunResult(failed, null, true, error);
        }

        try
        {
            var view = create(fallback.Renderer) ??
                throw new InvalidOperationException($"Renderer '{fallback.Renderer.Id}' returned no view.");
            catalog.ReportSuccess(fallback.Renderer.Id);
            return new RendererRunResult(fallback.Renderer, view, true, error);
        }
        catch (Exception fallbackError)
        {
            catalog.ReportFailure(fallback.Renderer.Id, fallbackError.Message);
            return new RendererRunResult(fallback.Renderer, null, true, error);
        }
    }
}
