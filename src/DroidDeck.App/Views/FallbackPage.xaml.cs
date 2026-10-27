using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>Shown instead of crashing when a navigation target is unknown.</summary>
public sealed partial class FallbackPage : Page
{
    /// <summary>Creates the page.</summary>
    public FallbackPage()
    {
        InitializeComponent();
    }

    /// <summary>Shows which navigation target was requested.</summary>
    public void SetRequestedPage(string key)
    {
        MessageText.Text = $"The page '{key}' is not available. Use the navigation pane to continue.";
    }
}
