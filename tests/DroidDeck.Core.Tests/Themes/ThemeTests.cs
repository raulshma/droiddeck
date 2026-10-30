using DroidDeck.Core.Configuration;
using DroidDeck.Core.Themes;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Themes;

public sealed class ThemeTests
{
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("high-contrast")]
    public void BuiltInThemesMeetContrastRequirements(string id)
    {
        var theme = ThemeCatalog.FindBuiltIn(id);

        Assert.NotNull(theme);
        var result = ThemeContrast.Validate(theme);
        Assert.True(result.IsValid, string.Join(" ", result.Findings.Where(f => f.IsError).Select(f => f.Explanation)));
        Assert.All(result.Findings, finding => Assert.True(finding.Ratio > 1));
    }

    [Fact]
    public void UnreadableThemeIsRejectedWithAnExplanation()
    {
        var theme = ThemeCatalog.Light.Clone();
        theme.Id = "unreadable";
        theme.Colors.Text = "#888888";
        theme.Colors.Background = "#7A7A7A";

        var result = ThemeContrast.Validate(theme);

        Assert.False(result.IsValid);
        var failure = Assert.Single(result.Findings, finding => finding.IsError && finding.Pair == "text on background");
        Assert.Contains("contrast ratio", failure.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.5:1", failure.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomThemeAppliesImmediatelyAndPersists()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);
        var changed = 0;
        manager.Changed += (_, _) => changed++;

        var theme = ThemeCatalog.Dark.Clone();
        theme.Id = "midnight";
        theme.Name = "Midnight";
        theme.Colors.Accent = "#FFB000";
        theme.Colors.AccentText = "#000000";
        theme.FontFamily = "Segoe UI Variable";
        theme.LogColors["error"] = "warning";

        var applied = await manager.SaveCustomAsync(theme);

        Assert.Equal("midnight", applied.Id);
        Assert.Equal(1, changed);
        Assert.Equal("midnight", configuration.GetString(SettingsPaths.ThemeId));
        Assert.Equal("warning", manager.GetCurrent().LogColors["error"]);
        Assert.True(File.Exists(Path.Combine(paths.ThemesDirectory, "midnight.json")));

        var restarted = new ThemeManager(paths, configuration);
        var current = restarted.GetCurrent();
        Assert.Equal("Midnight", current.Name);
        Assert.Equal("#FFB000", current.Colors.Accent);
    }

    [Fact]
    public async Task AccentAndReducedMotionPersistAndValidate()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);

        await manager.SetAccentAsync("#005A9E");
        await manager.SetReducedMotionAsync(true);

        Assert.Equal("#005A9E", manager.GetCurrent().Colors.Accent);
        Assert.True(manager.IsReducedMotion);
        Assert.Equal("reduced", configuration.GetString(SettingsPaths.Motion));

        await Assert.ThrowsAsync<ThemeValidationException>(() => manager.SetAccentAsync("#FEFEFE"));
    }

    [Fact]
    public async Task ThemeExportImportRoundTripsAndRejectsBadDocuments()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);
        var theme = ThemeCatalog.HighContrast.Clone();
        theme.Id = "contrast-plus";
        theme.Name = "Contrast plus";

        var json = ThemeManager.Export(theme);
        var imported = await manager.ImportAsync(json);

        Assert.Equal("contrast-plus", imported.Id);
        Assert.Equal("contrast-plus", manager.GetCurrent().Id);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.ImportAsync("{ not json"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.ImportAsync("""{ "id": "" }"""));
    }

    [Fact]
    public async Task LookupByIdentifierRejectsPathTraversalAndInvalidIds()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.Catalog.FindAsync("../light"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.Catalog.FindAsync("light\r"));
    }

    [Fact]
    public async Task UntrustedThemeIdentifierFromSettingsFallsBackToTheBasePalette()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        var manager = new ThemeManager(paths, configuration);

        await configuration.SetAsync(SettingsPaths.ThemeId, "../dark");
        Assert.Equal("dark", manager.GetCurrent(ThemeBase.Dark).Id);
    }

    [Fact]
    public async Task HandEditedThemeFileWithUnreadableColorsFallsBackToTheBasePalette()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        Directory.CreateDirectory(paths.ThemesDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(paths.ThemesDirectory, "hand-edited.json"),
            """
            { "id": "hand-edited", "name": "Hand edited", "base": "Dark",
              "colors": { "text": "#777777", "background": "#808080" } }
            """);

        var manager = new ThemeManager(paths, configuration);
        var current = manager.GetCurrent(ThemeBase.Dark);

        // The unreadable file is ignored and the dark built-in palette applies.
        Assert.Equal("dark", current.Id);
    }
}
