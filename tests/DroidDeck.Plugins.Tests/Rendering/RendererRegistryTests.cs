using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class RendererRegistryTests
{
    [Fact]
    public void PluginRenderersAreAttributedToTheirPlugin()
    {
        var registry = new PluginRegistry();

        registry.CreateScope("com.example").AddRenderer<FakeRenderer>();

        var registration = Assert.Single(registry.Renderers);
        Assert.Equal("com.example", registration.PluginId);
        Assert.Equal(typeof(FakeRenderer), registration.RendererType);
        Assert.Equal("com.example", registry.GetOwner(typeof(FakeRenderer)));
    }

    [Fact]
    public void BuiltInRenderersUseTheBuiltInIdentifier()
    {
        var registry = new PluginRegistry();

        registry.AddRenderer<FakeRenderer>();

        Assert.Equal(PluginConstants.BuiltInPluginId, Assert.Single(registry.Renderers).PluginId);
    }

    [Fact]
    public void RemovingAPluginDropsItsRenderers()
    {
        var registry = new PluginRegistry();
        registry.AddRenderer<FakeRenderer>();
        registry.CreateScope("com.example").AddRenderer<OtherRenderer>();

        var removed = registry.RemoveAll("com.example");

        Assert.Equal(1, removed);
        Assert.Equal(PluginConstants.BuiltInPluginId, Assert.Single(registry.Renderers).PluginId);
        Assert.Null(registry.GetOwner(typeof(OtherRenderer)));
    }

    [Fact]
    public void RendererTypesNeedAParameterlessConstructor()
    {
        var registry = new PluginRegistry();

        Assert.Throws<ArgumentException>(() => registry.AddRenderer(typeof(NoDefaultConstructorRenderer)));
        Assert.Throws<ArgumentException>(() => registry.AddRenderer(typeof(NotARenderer)));
        Assert.Empty(registry.Renderers);
    }

    [Fact]
    public void RegisteringARendererRaisesChanged()
    {
        var registry = new PluginRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;

        registry.CreateScope("com.example").AddRenderer<FakeRenderer>();
        registry.RemoveAll("com.example");

        Assert.Equal(2, changes);
    }

    public sealed class FakeRenderer : IRenderer
    {
        public string Id => "com.example.renderer.fake";

        public string DisplayName => "Fake renderer";

        public RendererKind Kind => RendererKind.LogDetail;

        public int Priority => 0;

        public bool CanRender(IRenderContext context) => true;

        public FrameworkElement CreateView(IRenderContext context) => null!;

        public void Dispose()
        {
        }
    }

    public sealed class OtherRenderer : IRenderer
    {
        public string Id => "com.example.renderer.other";

        public string DisplayName => "Other renderer";

        public RendererKind Kind => RendererKind.Chart;

        public int Priority => 0;

        public bool CanRender(IRenderContext context) => true;

        public FrameworkElement CreateView(IRenderContext context) => null!;

        public void Dispose()
        {
        }
    }

    private sealed class NotARenderer
    {
    }

    public sealed class NoDefaultConstructorRenderer : IRenderer
    {
        public NoDefaultConstructorRenderer(string value) => Value = value;

        public string Value { get; }

        public string Id => "com.example.renderer.noctor";

        public string DisplayName => "No constructor";

        public RendererKind Kind => RendererKind.Chart;

        public int Priority => 0;

        public bool CanRender(IRenderContext context) => true;

        public FrameworkElement CreateView(IRenderContext context) => null!;

        public void Dispose()
        {
        }
    }
}
