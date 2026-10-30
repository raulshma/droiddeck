using DroidDeck.Plugin.Abstractions.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>Shows a test outcome with its message, stack trace, and attachments.</summary>
public sealed class TestResultDetailRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.TestResultDetail.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.TestResultDetail.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.TestResult;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.TestResultDetail.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is TestResultData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (TestResultData)context.Data;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(RendererVisuals.Text(data.Name, fontSize: 13, bold: true));

        var succeeded = data.Status.Contains("pass", StringComparison.OrdinalIgnoreCase) ||
                        data.Status.Contains("success", StringComparison.OrdinalIgnoreCase);
        var status = RendererVisuals.Text(data.Status, fontSize: 12, bold: true);
        status.Foreground = succeeded ? RendererVisuals.SuccessBrush : RendererVisuals.CriticalBrush;
        panel.Children.Add(status);

        if (!string.IsNullOrWhiteSpace(data.Message))
        {
            panel.Children.Add(RendererVisuals.Text("Message", fontSize: 11, bold: true));
            panel.Children.Add(RendererVisuals.Text(data.Message, monospace: true));
        }

        if (!string.IsNullOrWhiteSpace(data.StackTrace))
        {
            panel.Children.Add(RendererVisuals.Text("Stack trace", fontSize: 11, bold: true));
            panel.Children.Add(RendererVisuals.Text(data.StackTrace, monospace: true, wrap: false));
        }

        if (data.Attachments is { Count: > 0 } attachments)
        {
            panel.Children.Add(RendererVisuals.Text("Attachments", fontSize: 11, bold: true));
            foreach (var attachment in attachments)
            {
                panel.Children.Add(RendererVisuals.Text("• " + attachment, fontSize: 12));
            }
        }

        return RendererVisuals.Scroll(panel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Shows labeled properties and optional JSON for inspector-style data.</summary>
public sealed class DataViewRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.DataView.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.DataView.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.DataView;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.DataView.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is DataViewData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (DataViewData)context.Data;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(RendererVisuals.Text(data.Title, fontSize: 13, bold: true));

        if (data.Properties is { Count: > 0 } properties)
        {
            var grid = new Grid { ColumnSpacing = 10, RowSpacing = 2 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var row = 0;
            foreach (var property in properties)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = RendererVisuals.Text(property.Name, fontSize: 12, bold: true);
                Grid.SetRow(name, row);
                Grid.SetColumn(name, 0);
                var value = RendererVisuals.Text(property.Value, monospace: true, fontSize: 12);
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);
                grid.Children.Add(name);
                grid.Children.Add(value);
                row++;
            }

            panel.Children.Add(grid);
        }

        if (!string.IsNullOrWhiteSpace(data.Json))
        {
            panel.Children.Add(RendererVisuals.Text("JSON", fontSize: 11, bold: true));
            panel.Children.Add(RendererVisuals.Text(
                RendererVisuals.TryFormatJson(data.Json) ?? data.Json,
                monospace: true,
                wrap: false));
        }

        return RendererVisuals.Scroll(panel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
