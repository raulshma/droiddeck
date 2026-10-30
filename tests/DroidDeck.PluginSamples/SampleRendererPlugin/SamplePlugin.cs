using System.Globalization;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SampleRendererPlugin;

/// <summary>
/// A sample plugin that contributes a log detail renderer and a chart renderer
/// through the same contracts the host built-ins use.
/// </summary>
public sealed class SampleRendererPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        context.Extensions.AddRenderer<SampleLogDetailRenderer>();
        context.Extensions.AddRenderer<SampleChartRenderer>();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Shows the selected log line with its level and tag as labeled fields.</summary>
public sealed class SampleLogDetailRenderer : IRenderer
{
    /// <summary>The stable renderer identifier.</summary>
    public const string RendererId = "com.example.sample.renderer.logDetail";

    /// <inheritdoc />
    public string Id => RendererId;

    /// <inheritdoc />
    public string DisplayName => "Sample log inspector";

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.LogDetail;

    /// <inheritdoc />
    public int Priority => 15;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is LogDetailData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (LogDetailData)context.Data;
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = "Sample log inspector",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.Create(
                CultureInfo.InvariantCulture,
                $"Level {data.Level ?? "?"}  \u00b7  {data.Tag ?? "no tag"}"),
            FontSize = 11,
            Opacity = 0.7,
        });
        panel.Children.Add(new TextBlock
        {
            Text = data.Line,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Lists the last value of every sampled series contributed to a chart.</summary>
public sealed class SampleChartRenderer : IRenderer
{
    /// <summary>The stable renderer identifier.</summary>
    public const string RendererId = "com.example.sample.renderer.chart";

    /// <inheritdoc />
    public string Id => RendererId;

    /// <inheritdoc />
    public string DisplayName => "Sample series summary";

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.Chart;

    /// <inheritdoc />
    public int Priority => 5;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is ChartData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (ChartData)context.Data;
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Sample chart: {data.Title}",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var series in data.Series)
        {
            var last = series.Values.Count > 0 ? series.Values[series.Values.Count - 1] : (double?)null;
            panel.Children.Add(new TextBlock
            {
                Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{series.Label}: {(last is { } value ? $"{value:0.#}" : "no data")}{data.Unit}"),
                FontSize = 12,
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "Host-sampled data rendered by the sample plugin.",
            FontSize = 11,
            Opacity = 0.7,
        });
        return panel;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
