using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginExtensionCatalogTests
{
    [Fact]
    public void DeviceActionsAreOrderedByPluginThenNameAndLabelTheirSource()
    {
        var registry = new PluginRegistry();
        registry.AddDeviceAction(new TestDeviceAction("builtin.action", "Zulu"));
        registry.CreateScope("com.example.beta").AddDeviceAction(new TestDeviceAction("beta.action", "Alpha"));
        registry.CreateScope("com.example.alpha").AddDeviceAction(new TestDeviceAction("alpha.second", "Bravo"));
        registry.CreateScope("com.example.alpha").AddDeviceAction(new TestDeviceAction("alpha.first", "Alpha"));
        var catalog = new PluginExtensionCatalog(registry);

        var actions = catalog.DeviceActions;

        Assert.Collection(
            actions,
            entry =>
            {
                Assert.Equal("alpha.first", entry.Action.Id);
                Assert.Equal("com.example.alpha", entry.PluginId);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("alpha.second", entry.Action.Id),
            entry => Assert.Equal("beta.action", entry.Action.Id),
            entry => Assert.Equal("builtin.action", entry.Action.Id));
        Assert.Equal("Built-in", actions[3].SourceLabel);
    }

    [Fact]
    public void ToolbarActionsAreOrderedByWeightThenPluginThenTitle()
    {
        var registry = new PluginRegistry();
        registry.AddToolbarAction(new TestToolbarAction("builtin.late", "Late", order: 20));
        registry.CreateScope("com.example.beta").AddToolbarAction(new TestToolbarAction("beta.early", "Beta early", 5));
        registry.CreateScope("com.example.alpha").AddToolbarAction(new TestToolbarAction("alpha.second", "Second", 5));
        registry.CreateScope("com.example.alpha").AddToolbarAction(new TestToolbarAction("alpha.first", "First", 5));
        var catalog = new PluginExtensionCatalog(registry);

        var actions = catalog.ToolbarActions;

        Assert.Collection(
            actions,
            entry => Assert.Equal("alpha.first", entry.Action.Id),
            entry => Assert.Equal("alpha.second", entry.Action.Id),
            entry => Assert.Equal("beta.early", entry.Action.Id),
            entry => Assert.Equal("builtin.late", entry.Action.Id));
        Assert.Equal("com.example.beta", actions[2].SourceLabel);
    }

    [Fact]
    public void DeviceTabsAreOrderedByWeightThenPluginThenTitle()
    {
        var registry = new PluginRegistry();
        registry.AddDeviceTab(new TestTab("builtin.default", "Overview", order: 0));
        registry.CreateScope("com.example.beta").AddDeviceTab(new TestTab("beta.tab", "Beta", 10));
        registry.CreateScope("com.example.alpha").AddDeviceTab(new TestTab("alpha.tab", "Zeta", 10));
        var catalog = new PluginExtensionCatalog(registry);

        var tabs = catalog.DeviceTabs;

        Assert.Collection(
            tabs,
            entry => Assert.Equal("builtin.default", entry.Tab.Id),
            entry =>
            {
                Assert.Equal("alpha.tab", entry.Tab.Id);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("beta.tab", entry.Tab.Id));
    }

    [Fact]
    public void CatalogReflectsLaterRegistrationsAndRemovals()
    {
        var registry = new PluginRegistry();
        var catalog = new PluginExtensionCatalog(registry);
        var scope = registry.CreateScope("com.example.plugin");

        scope.AddDeviceAction(new TestDeviceAction("plugin.action", "Plugin action"));
        Assert.Single(catalog.DeviceActions);

        registry.RemoveAll("com.example.plugin");
        Assert.Empty(catalog.DeviceActions);
    }

    [Fact]
    public void CatalogRejectsNullRegistry()
    {
        Assert.Throws<ArgumentNullException>(() => new PluginExtensionCatalog(null!));
    }

    private sealed class TestDeviceAction(string id, string name) : IDeviceAction
    {
        public string Id => id;

        public string Name => name;

        public bool RequiresConfirmation => false;

        public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestToolbarAction(string id, string title, int order) : IDeviceToolbarAction
    {
        public string Id => id;

        public string Title => title;

        public string? ToolTip => null;

        public int Order => order;

        public bool RequiresConfirmation => false;

        public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestTab(string id, string title, int order) : IDeviceTabProvider
    {
        public string Id => id;

        public string Title => title;

        public int Order => order;

        public object CreateContent(DeviceTabContext context) => title;
    }
}
