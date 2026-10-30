using DroidDeck.Plugin.Abstractions.Rendering;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>Draws host-sampled series as a line chart. It never polls the device.</summary>
public sealed class LineChartRenderer : IRenderer
{
    private Canvas? _canvas;
    private SizeChangedEventHandler? _sizeChanged;

    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.LineChart.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.LineChart.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.Chart;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.LineChart.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is ChartData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (ChartData)context.Data;
        var root = new Grid { MinHeight = 140 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var values = data.Series.Count > 0 ? data.Series[0].Values : null;
        var last = values is { Count: > 0 } ? values[values.Count - 1] : (double?)null;
        var legend = new TextBlock
        {
            Text = last is { } value
                ? $"{data.Title}: {value:0.#}{data.Unit}"
                : data.Title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        Microsoft.UI.Xaml.Controls.Grid.SetRow(legend, 0);
        root.Children.Add(legend);

        var canvas = new Canvas
        {
            MinHeight = 140,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        Microsoft.UI.Xaml.Controls.Grid.SetRow(canvas, 1);
        root.Children.Add(canvas);

        void Redraw()
        {
            canvas.Children.Clear();
            Draw(canvas, data);
        }

        _sizeChanged = (_, _) => Redraw();
        canvas.SizeChanged += _sizeChanged;
        _canvas = canvas;
        Redraw();
        return root;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_canvas is not null && _sizeChanged is not null)
        {
            _canvas.SizeChanged -= _sizeChanged;
        }

        _canvas = null;
        _sizeChanged = null;
    }

    private static void Draw(Canvas canvas, ChartData data)
    {
        var width = Math.Max(200, canvas.ActualWidth);
        var height = Math.Max(120, canvas.ActualHeight);
        const double top = 6;
        var plotHeight = height - 12;
        var range = data.Maximum - data.Minimum;
        if (range <= 0)
        {
            range = 1;
        }

        var gridBrush = RendererVisuals.GridLineBrush;
        for (var step = 0; step <= 4; step++)
        {
            var y = top + (plotHeight * step / 4);
            canvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1,
                Opacity = 0.35,
            });
        }

        var hasValues = false;
        var seriesIndex = 0;
        foreach (var series in data.Series)
        {
            if (series.Values.Count == 0)
            {
                continue;
            }

            hasValues = true;
            var brush = SeriesBrush(seriesIndex++);
            var points = new PointCollection();
            for (var index = 0; index < series.Values.Count; index++)
            {
                var x = series.Values.Count == 1
                    ? width / 2
                    : width * index / (series.Values.Count - 1);
                var normalized = Math.Clamp((series.Values[index] - data.Minimum) / range, 0, 1);
                points.Add(new Windows.Foundation.Point(x, top + plotHeight - (normalized * plotHeight)));
            }

            if (points.Count == 1)
            {
                var marker = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = brush,
                };
                Canvas.SetLeft(marker, points[0].X - 3);
                Canvas.SetTop(marker, points[0].Y - 3);
                canvas.Children.Add(marker);
            }
            else
            {
                canvas.Children.Add(new Polyline
                {
                    Points = points,
                    Stroke = brush,
                    StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round,
                });
            }
        }

        if (!hasValues)
        {
            var empty = new TextBlock
            {
                Text = "No samples to chart yet.",
                FontSize = 12,
                Opacity = 0.7,
            };
            Canvas.SetLeft(empty, 8);
            Canvas.SetTop(empty, 8);
            canvas.Children.Add(empty);
        }
    }

    private static Brush SeriesBrush(int index) => (index % 4) switch
    {
        0 => RendererVisuals.AccentBrush,
        1 => RendererVisuals.SuccessBrush,
        2 => RendererVisuals.CriticalBrush,
        _ => RendererVisuals.Brush("SystemFillColorCautionBrush", Colors.Goldenrod),
    };
}
