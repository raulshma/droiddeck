using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.Plugin.UI.Kit;

/// <summary>
/// A card matching the host's card style. Plugins compose content inside cards
/// to get the same surface, stroke, and corner radius the shell uses.
/// </summary>
public sealed class PluginCard : Grid
{
    private readonly Border _border = new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        BorderThickness = new Thickness(1),
    };
    private readonly ContentPresenter _presenter = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>Creates a card with an optional section title.</summary>
    public PluginCard(string? title = null)
    {
        Children.Add(_border);

        var panel = new StackPanel { Spacing = 10 };
        if (!string.IsNullOrWhiteSpace(title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
        }

        panel.Children.Add(_presenter);
        _border.Child = panel;

        ApplyTheme();
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>Gets or sets the card body.</summary>
    public object? Content
    {
        get => _presenter.Content;
        set => _presenter.Content = value;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void ApplyTheme()
    {
        _border.Background = RendererVisuals.Brush(
            "CardBackgroundFillColorDefaultBrush",
            Windows.UI.Color.FromArgb(14, 128, 128, 128));
        _border.BorderBrush = RendererVisuals.Brush(
            "CardStrokeColorDefaultBrush",
            Windows.UI.Color.FromArgb(46, 128, 128, 128));
    }
}
