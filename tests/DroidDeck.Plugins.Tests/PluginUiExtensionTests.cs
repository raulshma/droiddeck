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
    public void NewSurfacesReflectLaterRegistrationsAndRemovals()
    {
        var registry = new PluginRegistry();
        var catalog = new PluginExtensionCatalog(registry);
        var scope = registry.CreateScope("com.example.ui");
        var panel = new TestSessionPanel("ui.panel", "Plugin panel", 0);
        var section = new TestSettingsSection("ui.section", "Plugin section", 0);
        var statusItem = new TestStatusBarItem("ui.status", "Plugin status", 0);
        var dialog = new TestDialog("ui.dialog");

        scope.AddSessionPanel(panel);
        scope.AddSettingsSection(section);
        scope.AddStatusBarItem(statusItem);
        scope.AddDialog(dialog);
        Assert.Single(catalog.SessionPanels);
        Assert.Single(catalog.SettingsSections);
        Assert.Single(catalog.StatusBarItems);
        Assert.Single(registry.Dialogs);
        Assert.Equal("com.example.ui", registry.GetOwner(panel.GetType()));
        Assert.Equal("com.example.ui", registry.GetOwner(dialog.GetType()));

        registry.RemoveAll("com.example.ui");

        Assert.Empty(catalog.SessionPanels);
        Assert.Empty(catalog.SettingsSections);
        Assert.Empty(catalog.StatusBarItems);
        Assert.Empty(registry.Dialogs);
        Assert.Null(registry.GetOwner(panel.GetType()));
        Assert.Null(registry.GetOwner(dialog.GetType()));
    }

    [Fact]
    public void SessionPanelsSettingsSectionsAndStatusBarItemsAreOrderedStably()
    {
        var registry = new PluginRegistry();
        registry.AddStatusBarItem(new TestStatusBarItem("builtin.status", "Status", order: 0));
        registry.CreateScope("com.example.beta").AddSessionPanel(new TestSessionPanel("beta.panel", "Beta", 10));
        registry.CreateScope("com.example.alpha").AddSessionPanel(new TestSessionPanel("alpha.panel", "Zeta", 10));
        registry.CreateScope("com.example.alpha").AddSessionPanel(new TestSessionPanel("alpha.early", "Early", 5));
        registry.CreateScope("com.example.beta").AddSettingsSection(new TestSettingsSection("beta.section", "Beta", 20));
        registry.CreateScope("com.example.alpha").AddSettingsSection(new TestSettingsSection("alpha.section", "Alpha", 20));
        registry.CreateScope("com.example.beta").AddStatusBarItem(new TestStatusBarItem("beta.status", "Beta", 30));
        var catalog = new PluginExtensionCatalog(registry);

        Assert.Collection(
            catalog.SessionPanels,
            entry => Assert.Equal("alpha.early", entry.Panel.Id),
            entry =>
            {
                Assert.Equal("alpha.panel", entry.Panel.Id);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("beta.panel", entry.Panel.Id));
        Assert.Collection(
            catalog.SettingsSections,
            entry =>
            {
                Assert.Equal("alpha.section", entry.Section.Id);
                Assert.Equal("com.example.alpha", entry.SourceLabel);
            },
            entry => Assert.Equal("beta.section", entry.Section.Id));
        Assert.Collection(
            catalog.StatusBarItems,
            entry => Assert.Equal("builtin.status", entry.Item.Id),
            entry => Assert.Equal("beta.status", entry.Item.Id));
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
    public void GenericSugarStoresTypedUiEntries()
    {
        var registry = new PluginRegistry();

        registry.AddNavigationPage<SimplePage>();
        registry.AddProjectTab<SimpleProjectTab>();
        registry.AddHomeWidget<SimpleWidget>();
        registry.AddSessionPanel<SimpleSessionPanel>();
        registry.AddSettingsSection<SimpleSettingsSection>();
        registry.AddStatusBarItem<SimpleStatusBarItem>();
        registry.AddDialog<SimpleDialog>();

        Assert.Equal("generic.page", Assert.Single(registry.NavigationPages).Value.Id);
        Assert.Equal("generic.tab", Assert.Single(registry.ProjectTabs).Value.Id);
        Assert.Equal("generic.widget", Assert.Single(registry.HomeWidgets).Value.Id);
        Assert.Equal("generic.panel", Assert.Single(registry.SessionPanels).Value.Id);
        Assert.Equal("generic.section", Assert.Single(registry.SettingsSections).Value.Id);
        Assert.Equal("generic.status", Assert.Single(registry.StatusBarItems).Value.Id);
        Assert.Equal("generic.dialog", Assert.Single(registry.Dialogs).Value.Id);
    }

    [Fact]
    public void SessionPanelAndSettingsSectionRegistrationOrderOverridesTheDeclaredOrder()
    {
        var registry = new PluginRegistry();
        registry.CreateScope("com.example.ui").AddSessionPanel<SimpleSessionPanel>(order: 1);
        registry.CreateScope("com.example.later").AddSessionPanel(new TestSessionPanel("later.panel", "Later", 9));
        registry.CreateScope("com.example.ui").AddSettingsSection<SimpleSettingsSection>(order: 2);
        registry.CreateScope("com.example.later").AddSettingsSection(
            new TestSettingsSection("later.section", "Later", 8));
        var catalog = new PluginExtensionCatalog(registry);

        Assert.Collection(
            catalog.SessionPanels,
            entry =>
            {
                Assert.Equal(1, entry.Panel.Order);
                Assert.Equal("com.example.ui", entry.PluginId);
            },
            entry => Assert.Equal(9, entry.Panel.Order));
        Assert.Collection(
            catalog.SettingsSections,
            entry =>
            {
                Assert.Equal(2, entry.Section.Order);
                Assert.Equal("com.example.ui", entry.PluginId);
            },
            entry => Assert.Equal(8, entry.Section.Order));
    }

    [Fact]
    public void FindDialogResolvesByPluginScopeAndId()
    {
        var registry = new PluginRegistry();
        registry.CreateScope("com.example.alpha").AddDialog(new TestDialog("shared.dialog"));
        registry.CreateScope("com.example.beta").AddDialog(new TestDialog("shared.dialog"));
        registry.CreateScope("com.example.beta").AddDialog(new TestDialog("beta.only"));

        var alpha = registry.FindDialog("com.example.alpha", "shared.dialog");
        var beta = registry.FindDialog("com.example.beta", "shared.dialog");
        Assert.Equal("com.example.alpha", alpha?.PluginId);
        Assert.Equal("com.example.beta", beta?.PluginId);
        Assert.NotNull(registry.FindDialog("com.example.beta", "beta.only"));
        Assert.Null(registry.FindDialog("com.example.gamma", "shared.dialog"));
        Assert.Null(registry.FindDialog("com.example.beta", "missing.dialog"));
    }

    [Fact]
    public void RegistryRejectsNullUiExtensions()
    {
        var registry = new PluginRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.AddNavigationPage(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddProjectTab(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddHomeWidget(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddSessionPanel(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddSettingsSection(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddStatusBarItem(null!));
        Assert.Throws<ArgumentNullException>(() => registry.AddDialog(null!));
    }

    [Fact]
    public void TestRegistrySupportsUiSurfacesForHostPipelines()
    {
        var registry = new TestPluginRegistry();

        registry.AddNavigationPage(new TestPage("test.page", "Page"));
        registry.AddProjectTab(new TestProjectTab("test.tab", "Tab", 3));
        registry.AddHomeWidget(new TestWidget("test.widget", "Widget", 2));
        registry.AddSessionPanel(new TestSessionPanel("test.panel", "Panel", 4));
        registry.AddSettingsSection(new TestSettingsSection("test.section", "Section", 5));
        registry.AddStatusBarItem(new TestStatusBarItem("test.status", "Status", 6));
        registry.AddDialog(new TestDialog("test.dialog"));

        Assert.Equal("test.page", Assert.Single(registry.NavigationPages).Value.Id);
        Assert.Equal(3, Assert.Single(registry.ProjectTabs).Value.Order);
        Assert.Equal(2, Assert.Single(registry.HomeWidgets).Value.Order);
        Assert.Equal("test.panel", Assert.Single(registry.SessionPanels).Value.Id);
        Assert.Equal(5, Assert.Single(registry.SettingsSections).Value.Order);
        Assert.Equal(6, Assert.Single(registry.StatusBarItems).Value.Order);
        Assert.Equal("test.dialog", Assert.Single(registry.Dialogs).Value.Id);
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

    private sealed class TestSessionPanel(string id, string title, int order) : ISessionPanel
    {
        public string Id => id;

        public string Title => title;

        public int Order => order;

        public Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object>(title);

        public void UpdateSession(SessionPanelSnapshot snapshot)
        {
        }
    }

    private sealed class TestSettingsSection(string id, string title, int order) : ISettingsSection
    {
        public string Id => id;

        public string Title => title;

        public string? Description => null;

        public int Order => order;

        public Task<object> CreateViewAsync(ISettingsSectionContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object>(title);
    }

    private sealed class TestStatusBarItem(string id, string title, int order) : IStatusBarItem
    {
        public string Id => id;

        public string Title => title;

        public int Order => order;

        public string GetText() => title;
    }

    private sealed class TestDialog(string id) : IPluginDialog
    {
        public string Id => id;

        public string Title => "Test dialog";

        public IReadOnlyList<PluginDialogButton> Buttons { get; } = [new PluginDialogButton("ok", "OK", true)];

        public Task<object> CreateContentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>("content");

        public Task OnClosedAsync(string? buttonId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SimpleSessionPanel : ISessionPanel
    {
        public string Id => "generic.panel";

        public string Title => "Generic panel";

        public int Order => 0;

        public Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object>("view");

        public void UpdateSession(SessionPanelSnapshot snapshot)
        {
        }
    }

    private sealed class SimpleSettingsSection : ISettingsSection
    {
        public string Id => "generic.section";

        public string Title => "Generic section";

        public string? Description => null;

        public int Order => 0;

        public Task<object> CreateViewAsync(ISettingsSectionContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object>("view");
    }

    private sealed class SimpleStatusBarItem : IStatusBarItem
    {
        public string Id => "generic.status";

        public string Title => "Generic status";

        public int Order => 0;

        public string GetText() => "ready";
    }

    private sealed class SimpleDialog : IPluginDialog
    {
        public string Id => "generic.dialog";

        public string Title => "Generic dialog";

        public IReadOnlyList<PluginDialogButton> Buttons { get; } = [];

        public Task<object> CreateContentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>("view");

        public Task OnClosedAsync(string? buttonId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
