using DroidDeck.Core;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using DroidDeck.Plugins.BuiltIns;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class BuiltInRendererTests
{
    [Fact]
    public void BuiltInsCoverEveryRendererKind()
    {
        foreach (var kind in Enum.GetValues<RendererKind>())
        {
            Assert.Contains(
                BuiltInRendererDescriptors.All,
                descriptor => descriptor.Kind == kind && descriptor.IsBuiltIn);
        }
    }

    [Fact]
    public void BuiltInRendererIdsAreUniqueAndStable()
    {
        var ids = BuiltInRendererDescriptors.All.Select(descriptor => descriptor.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.StartsWith("droiddeck.builtin.renderer.", id, StringComparison.Ordinal));
        Assert.All(
            BuiltInRendererDescriptors.All,
            descriptor => Assert.Equal(PluginConstants.BuiltInPluginId, descriptor.PluginId));
    }

    [Fact]
    public void LogDetailBuiltInsCoverPlainJsonAndStackTraces()
    {
        var logDetail = BuiltInRendererDescriptors.All
            .Where(descriptor => descriptor.Kind == RendererKind.LogDetail)
            .ToArray();

        Assert.Contains(logDetail, descriptor => descriptor.Id == BuiltInRendererDescriptors.PlainLogDetail.Id);
        Assert.Contains(logDetail, descriptor => descriptor.Id == BuiltInRendererDescriptors.JsonLogDetail.Id);
        Assert.Contains(logDetail, descriptor => descriptor.Id == BuiltInRendererDescriptors.StackTraceLogDetail.Id);
    }

    [Fact]
    public void EveryBuiltInRendererMatchesItsDescriptor()
    {
        using var temp = new TempDirectory();
        var registry = new PluginRegistry();
        BuiltInExtensions.Register(registry, new AppPaths(temp.Combine("data")));

        Assert.Equal(BuiltInRenderers.Types.Count, registry.Renderers.Count);
        Assert.All(registry.Renderers, registration =>
            Assert.Equal(PluginConstants.BuiltInPluginId, registration.PluginId));
        Assert.Equal(
            BuiltInRenderers.Types.OrderBy(type => type.FullName, StringComparer.Ordinal),
            registry.Renderers.Select(registration => registration.RendererType)
                .OrderBy(type => type.FullName, StringComparer.Ordinal));

        var implementedKinds = new HashSet<RendererKind>();
        foreach (var registration in registry.Renderers)
        {
            var renderer = Assert.IsAssignableFrom<IRenderer>(
                Activator.CreateInstance(registration.RendererType));
            var descriptor = Assert.Single(
                BuiltInRendererDescriptors.All,
                candidate => candidate.Id == renderer.Id);
            Assert.Equal(descriptor.DisplayName, renderer.DisplayName);
            Assert.Equal(descriptor.Kind, renderer.Kind);
            Assert.Equal(descriptor.Priority, renderer.Priority);
            implementedKinds.Add(renderer.Kind);
            renderer.Dispose();
        }

        Assert.Equal(Enum.GetValues<RendererKind>().Order(), implementedKinds.Order());
    }
}
