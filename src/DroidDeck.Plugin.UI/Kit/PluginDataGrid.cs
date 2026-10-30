using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.Plugin.UI.Kit;

/// <summary>
/// A read-only data grid styled with host theme resources: a header row and
/// equal-width columns of selectable text, scrollable in both directions.
/// </summary>
public sealed class PluginDataGrid : UserControl
{
    private readonly Grid _grid = new();
    private string[] _columns = [];
    private IReadOnlyList<object?>[] _rows = [];

    /// <summary>Creates an empty data grid.</summary>
    public PluginDataGrid()
    {
        Content = new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0),
        };
    }

    /// <summary>Binds header cells and rows. Extra cells are ignored; missing cells stay empty.</summary>
    public void Bind(IReadOnlyList<string> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        if (columns.Count == 0)
        {
            throw new ArgumentException("A data grid needs at least one column.", nameof(columns));
        }

        _columns = columns.ToArray();
        _rows = rows.ToArray();
        Rebuild();
    }

    private void Rebuild()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        foreach (var _ in _columns)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }

        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var headerIndex in Enumerable.Range(0, _columns.Length))
        {
            var cell = CreateCell(
                _columns[headerIndex],
                isHeader: true,
                RendererVisuals.Brush(
                    "TextFillColorSecondaryBrush",
                    Windows.UI.Color.FromArgb(154, 0, 0, 0)));
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, headerIndex);
            _grid.Children.Add(cell);
        }

        for (var rowIndex = 0; rowIndex < _rows.Length; rowIndex++)
        {
            _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var row = _rows[rowIndex];
            for (var columnIndex = 0; columnIndex < _columns.Length; columnIndex++)
            {
                var value = columnIndex < row.Count ? row[columnIndex] : null;
                var cell = CreateCell(
                    value?.ToString() ?? string.Empty,
                    isHeader: false,
                    foreground: null);
                Grid.SetRow(cell, rowIndex + 1);
                Grid.SetColumn(cell, columnIndex);
                _grid.Children.Add(cell);
            }
        }
    }

    private static Border CreateCell(string text, bool isHeader, Brush? foreground)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = isHeader
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (foreground is not null)
        {
            block.Foreground = foreground;
        }

        return new Border
        {
            BorderBrush = RendererVisuals.Brush(
                "DividerStrokeColorDefaultBrush",
                Microsoft.UI.Colors.LightGray),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(8, 6, 8, 6),
            Child = block,
        };
    }
}
