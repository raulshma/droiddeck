using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeckPlugin;

/// <summary>Registers this plugin's contributions with DroidDeck.</summary>
public sealed class DroidDeckPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Extensions.AddRenderer<DroidDeckPluginRenderer>();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A renderer for log detail views. The host creates it on demand and owns the
/// returned element. Renderers are also listed under every View as menu.
/// </summary>
public sealed class DroidDeckPluginRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => "com.example.myplugin.renderer";

    /// <inheritdoc />
    public string DisplayName => "DroidDeckPlugin log detail";

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.LogDetail;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is LogDetailData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (LogDetailData)context.Data;
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock
        {
            Text = data.Tag ?? "No tag",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = data.Line,
            FontFamily = new FontFamily("Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return panel;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
