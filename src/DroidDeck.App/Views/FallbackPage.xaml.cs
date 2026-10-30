using DroidDeck.Core.Localization;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>Shown instead of crashing when a navigation target is unknown.</summary>
public sealed partial class FallbackPage : Page
{
    private readonly ILocalizer _localizer;

    /// <summary>Creates the page.</summary>
    public FallbackPage(ILocalizer localizer)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        InitializeComponent();
    }

    /// <summary>Shows which navigation target was requested.</summary>
    public void SetRequestedPage(string key)
    {
        MessageText.Text = _localizer.Format(LocalizationKeys.PageFallbackMessage, key);
    }
}
