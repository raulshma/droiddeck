using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>A renderer type contributed by a plugin or by the host.</summary>
/// <param name="PluginId">The contributing plugin.</param>
/// <param name="RendererType">The renderer type; the host creates instances on demand.</param>
public sealed record RendererRegistration(string PluginId, Type RendererType);

/// <summary>Reads the renderer types contributed through the extension registry.</summary>
public interface IRendererSource
{
    /// <summary>Gets the registered renderer types in registration order.</summary>
    IReadOnlyList<RendererRegistration> Renderers { get; }
}

/// <summary>Accepts renderer registrations. Implemented by the host extension registry.</summary>
public interface IRendererRegistrar : IRendererSource
{
    /// <summary>Registers a renderer type for on-demand creation.</summary>
    void AddRenderer(Type rendererType);
}

/// <summary>Registers renderers through the plugin extension registry.</summary>
public static class RendererRegistryExtensions
{
    /// <summary>Registers a renderer that the host creates when a view needs it.</summary>
    public static void AddRenderer<T>(this IPluginRegistry registry)
        where T : IRenderer, new() => AddRenderer(registry, typeof(T));

    /// <summary>Registers a renderer type that the host creates when a view needs it.</summary>
    public static void AddRenderer(this IPluginRegistry registry, Type rendererType)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rendererType);
        if (registry is not IRendererRegistrar registrar)
        {
            throw new InvalidOperationException(
                "This extension registry does not accept renderer contributions.");
        }

        registrar.AddRenderer(rendererType);
    }
}
