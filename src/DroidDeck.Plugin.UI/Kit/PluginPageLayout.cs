using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.Plugin.UI.Kit;

/// <summary>
/// A page shell for plugin navigation pages: a title and optional subtitle
/// over a stretchable content area, styled with host theme resources so it
/// matches the shell in light, dark, and high contrast.
/// </summary>
public sealed class PluginPageLayout : Grid
{
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly ContentPresenter _presenter = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>Creates a page layout.</summary>
    /// <param name="title">The page title.</param>
    /// <param name="subtitle">An optional one-line description.</param>
    public PluginPageLayout(string title, string? subtitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        RowSpacing = 12;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _title = new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _subtitle = new TextBlock
        {
            Text = subtitle ?? string.Empty,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible,
        };

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_title);
        header.Children.Add(_subtitle);
        Grid.SetRow(header, 0);
        Grid.SetRow(_presenter, 1);
        Children.Add(header);
        Children.Add(_presenter);

        ApplyTheme();
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>Gets or sets the page body.</summary>
    public object? Content
    {
        get => _presenter.Content;
        set => _presenter.Content = value;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void ApplyTheme() =>
        _subtitle.Foreground = RendererVisuals.Brush(
            "TextFillColorSecondaryBrush",
            Windows.UI.Color.FromArgb(154, 0, 0, 0));
}
