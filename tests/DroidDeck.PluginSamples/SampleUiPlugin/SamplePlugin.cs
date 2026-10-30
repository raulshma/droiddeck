using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;
using Microsoft.UI.Xaml.Controls;

namespace SampleUiPlugin;

/// <summary>
/// A sample plugin that contributes a navigation page, a project tab, and a
/// home widget composed from the host UI component kit, so they match host
/// styling in light, dark, and high contrast.
/// </summary>
public sealed class SampleUiPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.Extensions.AddNavigationPage(new SampleMetricsPage());
        context.Extensions.AddProjectTab(new SampleInsightsTab());
        context.Extensions.AddHomeWidget(new SampleBuildHealthWidget());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A navigation page showing build trend and matrix data from the kit.</summary>
public sealed class SampleMetricsPage : INavigationPage
{
    /// <summary>The stable page identifier.</summary>
    public const string PageId = "com.example.sample.ui.page";

    /// <inheritdoc />
    public string Id => PageId;

    /// <inheritdoc />
    public string Title => "Sample metrics";

    /// <inheritdoc />
    public string Icon => "\uE9D9";

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new PluginInfoBar();
        info.Show("All values come from the sample plugin.", InfoBarSeverity.Informational, "Sample data");

        var chart = new PluginMetricChart();
        chart.Plot([3, 5, 4, 8, 7, 10, 9], "CPU %");

        var grid = new PluginDataGrid();
        grid.Bind(
            ["Build", "Status", "Duration"],
            [
                ["debug", "ok", "12s"],
                ["release", "ok", "41s"],
                ["tests", "flaky", "1m 08s"],
            ]);

        var layout = new PluginPageLayout("Sample metrics", "Live view of the sample plugin's build data.")
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    info,
                    new PluginCard("Build trend") { Content = chart },
                    new PluginCard("Build matrix") { Content = grid },
                },
            },
        };
        return Task.FromResult<object>(layout);
    }
}

/// <summary>A project tab that summarizes the sample plugin's module data.</summary>
public sealed class SampleInsightsTab : IProjectTab
{
    /// <summary>The stable tab identifier.</summary>
    public const string TabId = "com.example.sample.ui.projectTab";

    /// <inheritdoc />
    public string Id => TabId;

    /// <inheritdoc />
    public string Title => "Sample insights";

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var grid = new PluginDataGrid();
        grid.Bind(
            ["Module", "Warnings", "Coverage"],
            [
                ["app", "0", "78%"],
                ["core", "2", "84%"],
                ["tools", "1", "61%"],
            ]);
        return Task.FromResult<object>(new PluginCard("Module insights") { Content = grid });
    }
}

/// <summary>A compact home widget with the sample build health trend.</summary>
public sealed class SampleBuildHealthWidget : IHomeWidget
{
    /// <summary>The stable widget identifier.</summary>
    public const string WidgetId = "com.example.sample.ui.homeWidget";

    /// <inheritdoc />
    public string Id => WidgetId;

    /// <inheritdoc />
    public string Title => "Build health";

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chart = new PluginMetricChart();
        chart.Plot([9, 8, 9, 7, 8, 9, 9], "Health");
        return Task.FromResult<object>(new PluginCard("Build health") { Content = chart });
    }
}
