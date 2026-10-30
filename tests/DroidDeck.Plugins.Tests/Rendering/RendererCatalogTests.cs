using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class RendererCatalogTests
{
    [Fact]
    public void SelectionPrefersTheHighestPriorityRendererOfTheKind()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        var plugin = Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example");
        catalog.Register(plugin);
        catalog.Register(Descriptor("plugin.log", RendererKind.LogDetail, priority: 100, pluginId: "com.example"));

        var selected = catalog.Select(RendererKind.Chart);

        Assert.Equal(plugin.Id, selected?.Id);
    }

    [Fact]
    public void SelectionUsesTheRememberedDefaultOverPriority()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));

        catalog.SetDefault(RendererKind.Chart, "builtin.chart");

        Assert.Equal("builtin.chart", catalog.Select(RendererKind.Chart)?.Id);
        Assert.Equal("builtin.chart", catalog.GetDefault(RendererKind.Chart));
    }

    [Fact]
    public void ItemOverrideWinsWithoutChangingTheDefault()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        catalog.SetDefault(RendererKind.Chart, "builtin.chart");

        var selected = catalog.Select(RendererKind.Chart, preferredId: "plugin.chart");

        Assert.Equal("plugin.chart", selected?.Id);
        Assert.Equal("builtin.chart", catalog.GetDefault(RendererKind.Chart));
    }

    [Fact]
    public void UnknownPreferredAndDefaultIdsAreIgnored()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.SetDefault(RendererKind.Chart, "uninstalled.renderer");

        Assert.Equal("builtin.chart", catalog.Select(RendererKind.Chart)?.Id);
        Assert.Equal(
            "builtin.chart",
            catalog.Select(RendererKind.Chart, preferredId: "uninstalled.renderer")?.Id);
    }

    [Fact]
    public void CanRenderFiltersCandidates()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.plain", RendererKind.LogDetail, priority: 0));
        catalog.Register(Descriptor("builtin.json", RendererKind.LogDetail, priority: 10));

        var selected = catalog.Select(
            RendererKind.LogDetail,
            canRender: descriptor => descriptor.Id != "builtin.json");

        Assert.Equal("builtin.plain", selected?.Id);
    }

    [Fact]
    public void ThreeConsecutiveFailuresDisableARenderer()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));

        catalog.ReportFailure("plugin.chart", "boom");
        catalog.ReportFailure("plugin.chart", "boom again");
        Assert.Equal(RendererHealth.Failing, catalog.Find("plugin.chart")?.Health);

        var disabled = catalog.ReportFailure("plugin.chart", "boom forever");

        Assert.True(disabled);
        var status = catalog.Find("plugin.chart");
        Assert.NotNull(status);
        Assert.Equal(RendererHealth.Disabled, status.Health);
        Assert.False(status.Enabled);
        Assert.Equal(3, status.FailureCount);
        Assert.Contains("boom forever", status.DisabledReason, StringComparison.Ordinal);
        Assert.Null(catalog.Select(RendererKind.Chart));
    }

    [Fact]
    public void SuccessResetsTheConsecutiveFailureCount()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        catalog.ReportFailure("plugin.chart", "boom");
        catalog.ReportFailure("plugin.chart", "boom");

        catalog.ReportSuccess("plugin.chart");

        var status = catalog.Find("plugin.chart");
        Assert.NotNull(status);
        Assert.Equal(0, status.FailureCount);
        Assert.Equal(RendererHealth.Healthy, status.Health);
        Assert.Equal("plugin.chart", catalog.Select(RendererKind.Chart)?.Id);
    }

    [Fact]
    public void ADisabledRendererLetsSelectionFallThroughToTheNextChoice()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        catalog.SetDefault(RendererKind.Chart, "plugin.chart");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            catalog.ReportFailure("plugin.chart", "boom");
        }

        Assert.Equal("builtin.chart", catalog.Select(RendererKind.Chart)?.Id);
    }

    [Fact]
    public void UsersCanDisableARendererWithoutUninstallingIt()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));

        catalog.SetEnabled("plugin.chart", false);
        Assert.Equal("builtin.chart", catalog.Select(RendererKind.Chart)?.Id);

        catalog.SetEnabled("plugin.chart", true);
        Assert.Equal("plugin.chart", catalog.Select(RendererKind.Chart)?.Id);
    }

    [Fact]
    public void RemovingAPluginKeepsBuiltIns()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        catalog.Register(Descriptor("plugin.log", RendererKind.LogDetail, priority: 50, pluginId: "com.example"));

        var removed = catalog.RemoveAll("com.example");

        Assert.Equal(2, removed);
        Assert.Equal("builtin.chart", Assert.Single(catalog.Statuses).Renderer.Id);

        catalog.RemoveAll(PluginConstantsBuiltIn);
        Assert.Single(catalog.Statuses);
    }

    [Fact]
    public void RemovingOneRendererDropsItsPreferenceAndKeepsBuiltIns()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        catalog.Register(Descriptor("plugin.log", RendererKind.LogDetail, priority: 50, pluginId: "com.example"));
        catalog.SetDefault(RendererKind.Chart, "plugin.chart");

        Assert.True(catalog.Remove("plugin.chart"));
        Assert.Null(catalog.Find("plugin.chart"));
        Assert.Equal("builtin.chart", catalog.Select(RendererKind.Chart)?.Id);
        Assert.Null(catalog.GetDefault(RendererKind.Chart));
        Assert.NotNull(catalog.Find("plugin.log"));

        Assert.False(catalog.Remove("plugin.chart"));
        Assert.False(catalog.Remove("builtin.chart"));
        Assert.NotNull(catalog.Find("builtin.chart"));
    }

    [Fact]
    public void RegisteringTheSameRendererAgainKeepsItsFailureState()
    {
        var catalog = new RendererCatalog();
        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));
        catalog.ReportFailure("plugin.chart", "boom");

        catalog.Register(Descriptor("plugin.chart", RendererKind.Chart, priority: 50, pluginId: "com.example"));

        Assert.Equal(1, catalog.Find("plugin.chart")?.FailureCount);
    }

    [Fact]
    public void SelectingAKindWithNoRenderersReturnsNull()
    {
        var catalog = new RendererCatalog();

        Assert.Null(catalog.Select(RendererKind.DataView));
    }

    [Fact]
    public void ChangedIsRaisedForCatalogMutations()
    {
        var catalog = new RendererCatalog();
        var changes = 0;
        catalog.Changed += (_, _) => changes++;

        catalog.Register(Descriptor("builtin.chart", RendererKind.Chart, priority: 0));
        catalog.SetDefault(RendererKind.Chart, "builtin.chart");
        catalog.ReportFailure("builtin.chart", "boom");
        catalog.SetEnabled("builtin.chart", false);
        catalog.RemoveAll(PluginConstantsBuiltIn);

        Assert.Equal(4, changes);
    }

    private const string PluginConstantsBuiltIn = PluginConstants.BuiltInPluginId;

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
