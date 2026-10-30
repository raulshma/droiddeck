using System.Runtime.InteropServices;
using DroidDeck.Core.Configuration;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace DroidDeck.App.Services;

/// <summary>Reports the theme the shell currently applies to renderer views.</summary>
public sealed class AppThemeService : IThemeService
{
    private AccessibilitySettings? _accessibility;
    private bool _accessibilityUnavailable;
    private bool? _darkOverride;

    /// <inheritdoc />
    public bool IsDark => _darkOverride ?? Application.Current?.RequestedTheme == ApplicationTheme.Dark;

    /// <inheritdoc />
    public bool IsHighContrast
    {
        get
        {
            if (_accessibilityUnavailable)
            {
                return false;
            }

            try
            {
                _accessibility ??= new AccessibilitySettings();
                return _accessibility.HighContrast;
            }
            catch (Exception exception) when (exception is InvalidOperationException or COMException)
            {
                _accessibilityUnavailable = true;
                return false;
            }
        }
    }

    /// <summary>Records the theme the settings choose; system follows Windows.</summary>
    public void Update(AppTheme theme) => _darkOverride = theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => null,
    };
}
