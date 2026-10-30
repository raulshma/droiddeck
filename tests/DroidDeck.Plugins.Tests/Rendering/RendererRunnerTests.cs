using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class RendererRunnerTests
{
    [Fact]
    public void RenderingCreatesTheSelectedRenderer()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));

        var result = RendererRunner.Render(
            catalog,
            RendererKind.Chart,
            preferredId: null,
            canRender: null,
            create: descriptor => $"view:{descriptor.Id}");

        Assert.Equal("builtin.chart", result.Renderer?.Id);
        Assert.Equal("view:builtin.chart", result.View);
        Assert.False(result.IsFallback);
        Assert.Null(result.Error);
        Assert.Equal(RendererHealth.Healthy, catalog.Find("builtin.chart")?.Health);
    }

    [Fact]
    public void AFailingRendererFallsBackToTheBuiltIn()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));

        var result = RendererRunner.Render(
            catalog,
            RendererKind.Chart,
            preferredId: null,
            canRender: null,
            create: descriptor => descriptor.Id == "plugin.chart"
                ? throw new InvalidOperationException("renderer broke")
                : $"view:{descriptor.Id}");

        Assert.True(result.IsFallback);
        Assert.Equal("builtin.chart", result.Renderer?.Id);
        Assert.Equal("view:builtin.chart", result.View);
        Assert.Equal("renderer broke", result.Error?.Message);
        Assert.Equal(1, catalog.Find("plugin.chart")?.FailureCount);
    }

    [Fact]
    public void RepeatedFailuresDisableTheRendererAndUseTheBuiltInDirectly()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        var attempts = 0;

        RendererRunner.Render(catalog, RendererKind.Chart, null, null, CreateBroken);
        RendererRunner.Render(catalog, RendererKind.Chart, null, null, CreateBroken);
        var third = RendererRunner.Render(catalog, RendererKind.Chart, null, null, CreateBroken);
        var fourth = RendererRunner.Render(catalog, RendererKind.Chart, null, null, CreateBroken);

        Assert.True(third.IsFallback);
        Assert.False(fourth.IsFallback);
        Assert.Equal("builtin.chart", fourth.Renderer?.Id);
        Assert.Equal(3, attempts);
        Assert.Equal(RendererHealth.Disabled, catalog.Find("plugin.chart")?.Health);

        string CreateBroken(RendererDescriptor descriptor)
        {
            if (descriptor.Id != "plugin.chart")
            {
                return $"view:{descriptor.Id}";
            }

            attempts++;
            throw new InvalidOperationException("boom");
        }
    }

    [Fact]
    public void UnavailableRenderersAreSkippedBeforeCreation()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.plain", RendererKind.LogDetail, priority: 0));
        catalog.Register(Descriptor("builtin.json", RendererKind.LogDetail, priority: 10));

        var result = RendererRunner.Render(
            catalog,
            RendererKind.LogDetail,
            preferredId: "builtin.json",
            canRender: descriptor => descriptor.Id != "builtin.json",
            create: descriptor => descriptor.Id);

        Assert.Equal("builtin.plain", result.Renderer?.Id);
        Assert.False(result.IsFallback);
        Assert.Null(result.Error);
    }

    [Fact]
    public void AThrowingCapabilityCheckIsTreatedAsUnavailable()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.plain", RendererKind.LogDetail, priority: 0));
        catalog.Register(Descriptor("plugin.log", RendererKind.LogDetail, priority: 50, pluginId: "com.example"));

        var result = RendererRunner.Render(
            catalog,
            RendererKind.LogDetail,
            preferredId: null,
            canRender: descriptor => descriptor.Id == "plugin.log"
                ? throw new InvalidOperationException("capability check broke")
                : true,
            create: descriptor => descriptor.Id);

        Assert.Equal("builtin.plain", result.Renderer?.Id);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ANullViewCountsAsAFailure()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));

        var result = RendererRunner.Render(
            catalog,
            RendererKind.Chart,
            null,
            null,
            create: _ => null);

        Assert.Null(result.View);
        Assert.NotNull(result.Error);
        Assert.Equal(1, catalog.Find("builtin.chart")?.FailureCount);
    }

    [Fact]
    public void RenderingWithoutCandidatesReturnsAnEmptyResult()
    {
        var catalog = new RendererCatalog();

        var result = RendererRunner.Render(catalog, RendererKind.FilePreview, null, null, _ => "view");

        Assert.Null(result.Renderer);
        Assert.Null(result.View);
        Assert.Null(result.Error);
        Assert.False(result.IsFallback);
    }

    [Fact]
    public void ABrokenBuiltInIsReportedWithoutThrowing()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));

        var result = RendererRunner.Render(
            catalog,
            RendererKind.Chart,
            null,
            null,
            create: _ => throw new InvalidOperationException("built-in broke"));

        Assert.True(result.IsFallback);
        Assert.Null(result.View);
        Assert.Equal("built-in broke", result.Error?.Message);
        Assert.Equal(1, catalog.Find("builtin.chart")?.FailureCount);
    }

    private static RendererDescriptor Descriptor(
        string id,
        RendererKind kind,
        int priority,
        string? pluginId = null) =>
        new(
            id,
            pluginId ?? PluginConstants.BuiltInPluginId,
            kind,
            id,
            priority,
            IsBuiltIn: pluginId is null);
}
