using System.Reflection;
using System.Runtime.Loader;

namespace DroidDeck.Plugins;

/// <summary>
/// Loads a plugin and its private dependencies in isolation while sharing
/// contract assemblies with the host so type identity is preserved.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DroidDeck.Plugin.Abstractions",
        "DroidDeck.Plugin.UI",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryAssemblyPath, string pluginId)
        : base($"DroidDeck.Plugin.{pluginId}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            // Returning null falls back to the default context, which owns the
            // contract assemblies and therefore preserves type identity.
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
