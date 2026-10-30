using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>Shared visual helpers for the host built-in renderers.</summary>
internal static class RendererVisuals
{
    private const int MaximumJsonLength = 256 * 1024;

    /// <summary>Creates a selectable, scrollable text view.</summary>
    public static ScrollViewer ScrollingText(string text, bool monospace = true, bool wrap = true)
    {
        var block = new TextBlock
        {
            Text = text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            FontSize = 12,
        };
        if (monospace)
        {
            block.FontFamily = new FontFamily("Consolas");
        }

        return Scroll(block);
    }

    /// <summary>Wraps content in a scroll viewer with sensible defaults.</summary>
    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(4),
    };

    /// <summary>Creates a theme-aware text block.</summary>
    public static TextBlock Text(
        string text,
        bool monospace = false,
        double fontSize = 12,
        bool wrap = true,
        bool bold = false) => new()
        {
            Text = text,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
            FontSize = fontSize,
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            FontFamily = monospace ? new FontFamily("Consolas") : new FontFamily("Segoe UI"),
        };

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Attempts to pretty-print the JSON embedded in a log line. Returns
    /// <see langword="null"/> when the text is not JSON or is unreasonably large.
    /// </summary>
    public static string? TryFormatJson(string text)
    {
        if (text.Length == 0 || text.Length > MaximumJsonLength)
        {
            return null;
        }

        var start = text.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return null;
        }

        var end = text.LastIndexOfAny(['}', ']']);
        if (end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                text[start..(end + 1)],
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return JsonSerializer.Serialize(document.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Resolves a theme resource brush, falling back to a palette color.</summary>
    public static Brush Brush(string resourceKey, Color fallback)
    {
        if (Application.Current?.Resources is { } resources &&
            resources.TryGetValue(resourceKey, out var value) &&
            value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    /// <summary>Gets the color used for a positive result.</summary>
    public static Brush SuccessBrush => Brush("SystemFillColorSuccessBrush", Colors.ForestGreen);

    /// <summary>Gets the color used for a failed result.</summary>
    public static Brush CriticalBrush => Brush("SystemFillColorCriticalBrush", Colors.Firebrick);

    /// <summary>Gets the color used for chart series.</summary>
    public static Brush AccentBrush => Brush("AccentFillColorDefaultBrush", Colors.DodgerBlue);

    /// <summary>Gets the color used for chart grid lines.</summary>
    public static Brush GridLineBrush => Brush("DividerStrokeColorDefaultBrush", Colors.LightGray);
}
