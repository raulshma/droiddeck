using DroidDeck.Core.Configuration;
using DroidDeck.Core.Themes;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Themes;

public sealed class HighContrastThemeTests
{
    [Fact]
    public async Task SystemHighContrastOverridesTheSelectedPaletteAndAccent()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);
        await configuration.SetAsync(SettingsPaths.Theme, "dark");
        await configuration.SetAsync(SettingsPaths.ThemeId, "dark");
        await configuration.SetAsync(SettingsPaths.AccentColor, "#FF0000");

        var theme = manager.GetCurrent(ThemeBase.HighContrast);

        Assert.Equal(ThemeCatalog.HighContrast.Id, theme.Id);
        Assert.Equal(ThemeBase.HighContrast, theme.Base);
        Assert.Equal(ThemeCatalog.HighContrast.Colors.Accent, theme.Colors.Accent);
        Assert.True(theme.ReducedMotion);
    }

    [Fact]
    public async Task SystemHighContrastKeepsTheConfiguredFont()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);
        await configuration.SetAsync(SettingsPaths.FontFamily, "Atkinson Hyperlegible");

        var theme = manager.GetCurrent(ThemeBase.HighContrast);

        Assert.Equal("Atkinson Hyperlegible", theme.FontFamily);
    }

    [Fact]
    public async Task HighContrastBaseReducesMotionUnlessTheThemeOptsOut()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);

        Assert.True(manager.IsReducedMotion(ThemeBase.HighContrast));
        Assert.False(manager.IsReducedMotion(ThemeBase.Light));

        await manager.SetReducedMotionAsync(true);
        Assert.True(manager.IsReducedMotion(ThemeBase.Light));
    }
}
