using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginUiExtensionTests
{
    [Fact]
    public void NavigationPagesAreOrderedByPluginThenTitleAndLabelTheirSource()
    {
        var registry = new PluginRegistry();
        registry.AddNavigationPage(new TestPage("builtin.page", "Zulu page"));
        registry.CreateScope("com.example.beta").AddNavigationPage(new TestPage("beta.page", "Alpha page"));
        registry.CreateScope("com.example.alpha").AddNavigationPage(new TestPage("alpha.page", "Bravo page"));
        var catalog = new PluginExtensionCatalog(registry);

        var pages = catalog.NavigationPages;

        Assert.Collection(
            pages,
            entry =>
            {
                Assert.Equal("alpha.page", entry.Page.Id);
                Assert.Equal("com.example.alpha", entry.PluginId);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("beta.page", entry.Page.Id),
            entry => Assert.Equal("builtin.page", entry.Page.Id));
        Assert.Equal("Built-in", pages[2].SourceLabel);
    }

    [Fact]
    public void ProjectTabsAreOrderedByWeightThenPluginThenTitle()
    {
        var registry = new PluginRegistry();
        registry.AddProjectTab(new TestProjectTab("builtin.tab", "Overview", order: 0));
        registry.CreateScope("com.example.beta").AddProjectTab(new TestProjectTab("beta.tab", "Beta", 10));
        registry.CreateScope("com.example.alpha").AddProjectTab(new TestProjectTab("alpha.tab", "Zeta", 10));
        registry.CreateScope("com.example.alpha").AddProjectTab(new TestProjectTab("alpha.early", "Early", 5));
        var catalog = new PluginExtensionCatalog(registry);

        var tabs = catalog.ProjectTabs;

        Assert.Collection(
            tabs,
            entry => Assert.Equal("builtin.tab", entry.Tab.Id),
            entry => Assert.Equal("alpha.early", entry.Tab.Id),
            entry =>
            {
                Assert.Equal("alpha.tab", entry.Tab.Id);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("beta.tab", entry.Tab.Id));
    }

    [Fact]
    public void HomeWidgetsAreOrderedByWeightThenPluginThenTitle()
    {
        var registry = new PluginRegistry();
        registry.AddHomeWidget(new TestWidget("builtin.widget", "Status", order: 10));
        registry.CreateScope("com.example.beta").AddHomeWidget(new TestWidget("beta.widget", "Beta", 20));
        registry.CreateScope("com.example.alpha").AddHomeWidget(new TestWidget("alpha.widget", "Alpha", 20));
        var catalog = new PluginExtensionCatalog(registry);

        var widgets = catalog.HomeWidgets;

        Assert.Collection(
            widgets,
            entry => Assert.Equal("builtin.widget", entry.Widget.Id),
            entry =>
            {
                Assert.Equal("alpha.widget", entry.Widget.Id);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("beta.widget", entry.Widget.Id));
    }

    [Fact]
    public void UiSurfacesReflectLaterRegistrationsAndRemovals()
    {
        var registry = new PluginRegistry();
        var catalog = new PluginExtensionCatalog(registry);
        var scope = registry.CreateScope("com.example.ui");
        var page = new TestPage("ui.page", "Plugin page");

        scope.AddNavigationPage(page);
        scope.AddProjectTab(new TestProjectTab("ui.tab", "Plugin tab", 0));
        scope.AddHomeWidget(new TestWidget("ui.widget", "Plugin widget", 0));
        Assert.Single(catalog.NavigationPages);
        Assert.Single(catalog.ProjectTabs);
        Assert.Single(catalog.HomeWidgets);
        Assert.Equal("com.example.ui", registry.GetOwner(page.GetType()));

        registry.RemoveAll("com.example.ui");

        Assert.Empty(catalog.NavigationPages);
        Assert.Empty(catalog.ProjectTabs);
        Assert.Empty(catalog.HomeWidgets);
        Assert.Null(registry.GetOwner(page.GetType()));
    }

    [Fact]
    public void GenericSugarStoresTypedUiEntries()
    {
        var registry = new PluginRegistry();

        registry.AddNavigationPage<SimplePage>();
        registry.AddProjectTab<SimpleProjectTab>();
        registry.AddHomeWidget<SimpleWidget>();

        Assert.Equal("generic.page", Assert.Single(registry.NavigationPages).Value.Id);
        Assert.Equal("generic.tab", Assert.Single(registry.ProjectTabs).Value.Id);
        Assert.Equal("generic.widget", Assert.Single(registry.HomeWidgets).Value.Id);
    }

    [Fact]
    public void HomeWidgetRegistrationOrderOverridesTheDeclaredOrder()
    {
        var registry = new PluginRegistry();
        registry.CreateScope("com.example.ui").AddHomeWidget<SimpleWidget>(order: 1);
        registry.CreateScope("com.example.later").AddHomeWidget(new TestWidget("later.widget", "Later", 9));
        var catalog = new PluginExtensionCatalog(registry);

        Assert.Collection(
            catalog.HomeWidgets,
            entry =>
            {
                Assert.Equal(1, entry.Widget.Order);
                Assert.Equal("com.example.ui", entry.PluginId);
            },
            entry =>
            {
                Assert.Equal(9, entry.Widget.Order);
                Assert.Equal("com.example.later", entry.PluginId);
            });
    }

    [Fact]
    public void RegistryRejectsNullUiExtensions()
    {
        var registry = new PluginRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.AddNavigationPage(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddProjectTab(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddHomeWidget(null!));
    }

    [Fact]
    public void TestRegistrySupportsUiSurfacesForHostPipelines()
    {
        var registry = new TestPluginRegistry();

        registry.AddNavigationPage(new TestPage("test.page", "Page"));
        registry.AddProjectTab(new TestProjectTab("test.tab", "Tab", 3));
        registry.AddHomeWidget(new TestWidget("test.widget", "Widget", 2));

        Assert.Equal("test.page", Assert.Single(registry.NavigationPages).Value.Id);
        Assert.Equal(3, Assert.Single(registry.ProjectTabs).Value.Order);
        Assert.Equal(2, Assert.Single(registry.HomeWidgets).Value.Order);
    }

    private sealed class TestPage(string id, string title) : INavigationPage
    {
        public string Id => id;

        public string Title => title;

        public string Icon => string.Empty;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(title);
    }

    private sealed class TestProjectTab(string id, string title, int order) : IProjectTab
    {
        public string Id => id;

        public string Title => title;

        public int Order => order;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(title);
    }

    private sealed class TestWidget(string id, string title, int order) : IHomeWidget
    {
        public string Id => id;

        public string Title => title;

        public int Order => order;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) => Task.FromResult<object>(title);
    }

    private sealed class SimplePage : INavigationPage
    {
        public string Id => "generic.page";

        public string Title => "Generic page";

        public string Icon => string.Empty;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) => Task.FromResult<object>("view");
    }

    private sealed class SimpleProjectTab : IProjectTab
    {
        public string Id => "generic.tab";

        public string Title => "Generic tab";

        public int Order => 0;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) => Task.FromResult<object>("view");
    }

    private sealed class SimpleWidget : IHomeWidget
    {
        public string Id => "generic.widget";

        public string Title => "Generic widget";

        public int Order => 99;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) => Task.FromResult<object>("view");
    }
}
