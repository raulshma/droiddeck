using DroidDeck.Core.Configuration;
using DroidDeck.Core.Themes;

namespace DroidDeck.Core.Tests.Themes;

public sealed class ShellThemePolicyTests
{
    [Theory]
    [InlineData(AppTheme.System, ThemeBase.Light, ShellPalette.Light, true)]
    [InlineData(AppTheme.System, ThemeBase.Dark, ShellPalette.Dark, true)]
    [InlineData(AppTheme.Dark, ThemeBase.Light, ShellPalette.Dark, true)]
    [InlineData(AppTheme.Light, ThemeBase.Dark, ShellPalette.Light, true)]
    [InlineData(AppTheme.System, ThemeBase.HighContrast, ShellPalette.System, false)]
    [InlineData(AppTheme.Dark, ThemeBase.HighContrast, ShellPalette.System, false)]
    [InlineData(AppTheme.Light, ThemeBase.HighContrast, ShellPalette.System, false)]
    public void HighContrastFollowsTheSystemAndSkipsCustomTokens(
        AppTheme preference,
        ThemeBase themeBase,
        ShellPalette expectedPalette,
        bool expectedCustomTokens)
    {
        Assert.Equal(expectedPalette, ShellThemePolicy.ResolvePalette(preference, themeBase));
        Assert.Equal(expectedCustomTokens, ShellThemePolicy.ApplyCustomTokens(themeBase));
    }
}
