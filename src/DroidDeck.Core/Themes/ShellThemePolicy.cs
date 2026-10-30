using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Themes;

/// <summary>The element palette the shell requests from WinUI.</summary>
public enum ShellPalette
{
    /// <summary>Let WinUI follow the operating system, including high contrast.</summary>
    System,

    /// <summary>Request the light element theme.</summary>
    Light,

    /// <summary>Request the dark element theme.</summary>
    Dark,
}

/// <summary>
/// Decides how the shell maps a theme preference onto WinUI resources. High
/// contrast is owned by the operating system, so custom tokens must not
/// override the system palette.
/// </summary>
public static class ShellThemePolicy
{
    /// <summary>Gets the element palette the shell should request.</summary>
    public static ShellPalette ResolvePalette(AppTheme preference, ThemeBase themeBase)
    {
        if (themeBase == ThemeBase.HighContrast)
        {
            return ShellPalette.System;
        }

        return preference switch
        {
            AppTheme.Dark => ShellPalette.Dark,
            AppTheme.Light => ShellPalette.Light,
            _ => themeBase == ThemeBase.Dark ? ShellPalette.Dark : ShellPalette.Light,
        };
    }

    /// <summary>Gets whether the shell may apply custom color tokens.</summary>
    public static bool ApplyCustomTokens(ThemeBase themeBase) => themeBase != ThemeBase.HighContrast;
}
