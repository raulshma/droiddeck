using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DroidDeck.Plugin.UI.Kit;

/// <summary>
/// A minimal sparkline chart drawn with theme-aware shapes: horizontal grid
/// lines, an accent-colored series line, and a legend showing the latest value.
/// </summary>
public sealed class PluginMetricChart : UserControl
{
    private static readonly double[] GridLineRatios = [0.25, 0.5, 0.75];
    private const double DefaultWidth = 320;
    private const double DefaultHeight = 96;

    private readonly Canvas _canvas = new();
    private readonly TextBlock _legend = new() { FontSize = 11 };
    private double[] _values = [];
    private string? _label;

    /// <summary>Creates an empty chart.</summary>
    public PluginMetricChart()
    {
        var rows = new Grid { RowSpacing = 4 };
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _canvas.MinHeight = DefaultHeight;
        Grid.SetRow(_canvas, 0);
        Grid.SetRow(_legend, 1);
        rows.Children.Add(_canvas);
        rows.Children.Add(_legend);
        Content = rows;

        SizeChanged += OnSizeChanged;
        Render();
    }

    /// <summary>Plots a series with an optional legend label.</summary>
    public void Plot(IReadOnlyList<double> values, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values.ToArray();
        _label = label;
        Render();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Render();

    private void Render()
    {
        var accent = RendererVisuals.AccentBrush;
        var gridLine = RendererVisuals.GridLineBrush;
        _canvas.Children.Clear();
        var width = ActualWidth > 0 ? ActualWidth : DefaultWidth;
        var height = ActualHeight > 0 ? ActualHeight : DefaultHeight;

        foreach (var ratio in GridLineRatios)
        {
            var y = height * ratio;
            _canvas.Children.Add(new Line
            {
                X1 = 0,
                Y1 = y,
                X2 = width,
                Y2 = y,
                Stroke = gridLine,
                StrokeThickness = 1,
                Opacity = 0.5,
            });
        }

        if (_values.Length == 0)
        {
            _legend.Text = "No data yet.";
            _legend.Foreground = RendererVisuals.Brush(
                "TextFillColorSecondaryBrush",
                Color.FromArgb(154, 0, 0, 0));
            return;
        }

        var min = _values.Min();
        var max = _values.Max();
        var span = Math.Max(max - min, double.Epsilon);
        var inset = 4d;
        var plotWidth = Math.Max(width - (inset * 2), 1);
        var plotHeight = Math.Max(height - (inset * 2), 1);
        var points = new PointCollection();
        for (var index = 0; index < _values.Length; index++)
        {
            var x = _values.Length == 1
                ? plotWidth / 2
                : plotWidth * index / (_values.Length - 1);
            var y = plotHeight - (plotHeight * (_values[index] - min) / span);
            points.Add(new Windows.Foundation.Point((float)(inset + x), (float)(inset + y)));
        }

        _canvas.Children.Add(new Polyline
        {
            Points = points,
            Stroke = accent,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        });

        var last = points[^1];
        var marker = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = accent,
        };
        Canvas.SetLeft(marker, last.X - 3);
        Canvas.SetTop(marker, last.Y - 3);
        _canvas.Children.Add(marker);

        var latest = _values[^1];
        _legend.Text = _label is null
            ? $"latest {latest:0.##}"
            : $"{_label} · latest {latest:0.##}";
        _legend.Foreground = accent;
    }
}
