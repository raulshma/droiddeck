using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Loads the sample UI plugin end to end: the plugin contributes a navigation
/// page, a project tab, and a home widget through the same registry the host
/// built-ins use. The views themselves are WinUI objects, which the xunit host
/// cannot construct, so this asserts the registration and metadata path.
/// </summary>
public sealed class SampleUiPluginTests
{
    private const string PluginId = "com.example.sample.ui";

    [Fact]
    public async Task ASamplePluginContributesAPageAProjectTabAndAHomeWidget()
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

        Assert.Equal(PluginId, registry.GetOwner(page.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(tab.Value.GetType()));
        Assert.Equal(PluginId, registry.GetOwner(widget.Value.GetType()));

        var catalog = new PluginExtensionCatalog(registry);
        Assert.Equal("Sample metrics", Assert.Single(catalog.NavigationPages).Page.Title);
        Assert.Equal("Sample insights", Assert.Single(catalog.ProjectTabs).Tab.Title);
        Assert.Equal("Build health", Assert.Single(catalog.HomeWidgets).Widget.Title);
        Assert.Equal(PluginId, Assert.Single(catalog.NavigationPages).SourceLabel);
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
            Capabilities = ["ui-page", "project-tab", "home-widget"],
        };
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }
}
