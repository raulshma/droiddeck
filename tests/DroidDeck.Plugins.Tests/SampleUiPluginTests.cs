using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Loads the sample UI plugin end to end: the plugin contributes every
/// host-managed UI surface through the same registry the host built-ins use.
/// The views themselves are WinUI objects, which the xunit host cannot
/// construct, so this asserts the registration and metadata path.
/// </summary>
public sealed class SampleUiPluginTests
{
    private const string PluginId = "com.example.sample.ui";

    [Fact]
    public async Task ASamplePluginContributesEveryUiSurface()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        var registry = new PluginRegistry();
        await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");

        var result = await loader.LoadAsync(directory);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Validation.Issues.Select(issue => issue.ToString())));

        var page = Assert.Single(registry.NavigationPages);
        Assert.Equal(PluginId, page.PluginId);
        Assert.Equal("com.example.sample.ui.page", page.Value.Id);
        Assert.Equal("Sample metrics", page.Value.Title);
        Assert.NotEmpty(page.Value.Icon);

        var tab = Assert.Single(registry.ProjectTabs);
        Assert.Equal(PluginId, tab.PluginId);
        Assert.Equal("com.example.sample.ui.projectTab", tab.Value.Id);
        Assert.Equal("Sample insights", tab.Value.Title);
        Assert.Equal(50, tab.Value.Order);

        var widget = Assert.Single(registry.HomeWidgets);
        Assert.Equal(PluginId, widget.PluginId);
        Assert.Equal("com.example.sample.ui.homeWidget", widget.Value.Id);
        Assert.Equal("Build health", widget.Value.Title);
        Assert.Equal(40, widget.Value.Order);

        var panel = Assert.Single(registry.SessionPanels);
        Assert.Equal(PluginId, panel.PluginId);
        Assert.Equal("com.example.sample.ui.sessionPanel", panel.Value.Id);
        Assert.Equal("Session monitor", panel.Value.Title);

        var section = Assert.Single(registry.SettingsSections);
        Assert.Equal(PluginId, section.PluginId);
        Assert.Equal("com.example.sample.ui.settingsSection", section.Value.Id);

        var statusItem = Assert.Single(registry.StatusBarItems);
        Assert.Equal(PluginId, statusItem.PluginId);
        Assert.Equal("com.example.sample.ui.statusItem", statusItem.Value.Id);
        Assert.IsAssignableFrom<IStatusBarClickableItem>(statusItem.Value);

        var toolbar = Assert.Single(registry.ToolbarActions);
        Assert.Equal(PluginId, toolbar.PluginId);
        Assert.Equal("com.example.sample.ui.toolbar", toolbar.Value.Id);

        var command = Assert.Single(registry.Commands);
        Assert.Equal(PluginId, command.PluginId);
        Assert.Equal("com.example.sample.ui.showDialog", command.Value.Id);

        var dialog = Assert.Single(registry.Dialogs);
        Assert.Equal(PluginId, dialog.PluginId);
        Assert.Equal("com.example.sample.ui.dialog", dialog.Value.Id);
        Assert.NotNull(registry.FindDialog(PluginId, "com.example.sample.ui.dialog"));

        Assert.Equal(PluginId, registry.GetOwner(page.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(tab.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(widget.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(panel.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(section.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(dialog.Value.GetType()));

        var catalog = new PluginExtensionCatalog(registry);
        Assert.Equal("Sample metrics", Assert.Single(catalog.NavigationPages).Page.Title);
        Assert.Equal("Sample insights", Assert.Single(catalog.ProjectTabs).Tab.Title);
        Assert.Equal("Build health", Assert.Single(catalog.HomeWidgets).Widget.Title);
        Assert.Equal("Session monitor", Assert.Single(catalog.SessionPanels).Panel.Title);
        Assert.Equal("Sample run options", Assert.Single(catalog.SettingsSections).Section.Title);
        Assert.Equal("com.example.sample.ui.statusItem", Assert.Single(catalog.StatusBarItems).Item.Id);
        Assert.Equal(PluginId, Assert.Single(catalog.NavigationPages).SourceLabel);
    }

    [Fact]
    public async Task RemovingTheSamplePluginRemovesEverySurface()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        var registry = new PluginRegistry();
        await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");
        var load = await loader.LoadAsync(directory);
        Assert.True(load.Succeeded);

        registry.RemoveAll(PluginId);

        Assert.Empty(registry.NavigationPages);
        Assert.Empty(registry.ProjectTabs);
        Assert.Empty(registry.HomeWidgets);
        Assert.Empty(registry.SessionPanels);
        Assert.Empty(registry.SettingsSections);
        Assert.Empty(registry.StatusBarItems);
        Assert.Empty(registry.ToolbarActions);
        Assert.Empty(registry.Commands);
        Assert.Empty(registry.Dialogs);
    }

    private static string StagePlugin(TempDirectory temp)
    {
        var directory = temp.CreateDirectory("plugins", PluginId);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "SampleUiPlugin.dll"),
            Path.Combine(directory, "SampleUiPlugin.dll"),
            overwrite: true);

        var manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Sample UI Plugin",
            Version = "1.0.0",
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SampleUiPlugin.dll",
            EntryType = "SampleUiPlugin.SampleUiPlugin",
            Capabilities =
            [
                "ui-page",
                "project-tab",
                "home-widget",
                "session-panel",
                "settings-section",
                "status-bar-item",
                "toolbar-action",
                "command",
                "plugin-dialog",
            ],
        };
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }
}
