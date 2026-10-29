using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DroidDeck.App.Controls;

/// <summary>
/// Draws a lightweight sparkline for a sampled series. Points are normalized into
/// the control bounds and redrawn only when the page pushes a new sample.
/// </summary>
public sealed partial class PerformanceSparkline : UserControl
{
    /// <summary>Identifies the line color.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(PerformanceSparkline),
        new PropertyMetadata(null, OnStrokeChanged));

    private readonly List<double?> _values = [];
    private double? _minimum;
    private double? _maximum;

    /// <summary>Creates the control.</summary>
    public PerformanceSparkline()
    {
        InitializeComponent();
    }

    /// <summary>Gets or sets the line color.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Replaces the series and optionally fixes the vertical range.</summary>
    public void SetValues(IReadOnlyList<double?> values, double? minimum = null, double? maximum = null)
    {
        _values.Clear();
        _values.AddRange(values);
        _minimum = minimum;
        _maximum = maximum;
        Render();
    }

    /// <summary>Clears the series.</summary>
    public void Clear()
    {
        _values.Clear();
        Render();
    }

    private static void OnStrokeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is PerformanceSparkline chart)
        {
            chart.Render();
        }
    }

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    private void Render()
    {
        Plot.Children.Clear();
        var values = _values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        EmptyText.Visibility = values.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (values.Length == 0 || Plot.ActualWidth <= 1 || Plot.ActualHeight <= 1)
        {
            return;
        }

        var stroke = Stroke ?? new SolidColorBrush(Colors.DodgerBlue);
        var width = Plot.ActualWidth;
        var height = Plot.ActualHeight;
        const double padding = 4;
        var usableWidth = Math.Max(1, width - (padding * 2));
        var usableHeight = Math.Max(1, height - (padding * 2));

        var minimum = _minimum ?? values.Min();
        var maximum = _maximum ?? values.Max();
        if (maximum <= minimum)
        {
            maximum = minimum + 1;
        }

        var points = new PointCollection();
        for (var index = 0; index < values.Length; index++)
        {
            var x = padding + (values.Length == 1 ? usableWidth / 2 : usableWidth * index / (values.Length - 1));
            var normalized = Math.Clamp((values[index] - minimum) / (maximum - minimum), 0, 1);
            var y = padding + (usableHeight * (1 - normalized));
            points.Add(new Point(x, y));
        }

        if (points.Count >= 2)
        {
            var area = new Polygon
            {
                Fill = CreateAreaBrush(stroke),
                Points =
                [
                    new Point(points[0].X, height - padding),
                    .. points,
                    new Point(points[^1].X, height - padding),
                ],
            };
            Plot.Children.Add(area);

            Plot.Children.Add(new Polyline
            {
                Stroke = stroke,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                Points = points,
            });
        }

        var marker = new Ellipse
        {
            Width = 5,
            Height = 5,
            Fill = stroke,
        };
        Plot.Children.Add(marker);
        Canvas.SetLeft(marker, points[^1].X - 2.5);
        Canvas.SetTop(marker, points[^1].Y - 2.5);
    }

    private static SolidColorBrush CreateAreaBrush(Brush stroke)
    {
        if (stroke is SolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color) { Opacity = 0.14 };
        }

        return new SolidColorBrush(Colors.Transparent);
    }
}
