using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class SettingsCatalogTests
{
    [Theory]
    [InlineData("theme", SettingsPaths.Theme)]
    [InlineData("Theme", SettingsPaths.Theme)]
    [InlineData("MAX SIZE", SettingsPaths.ScreenMaxSize)]
    [InlineData("concurrent mirrors", SettingsPaths.ScreenMaxMirrors)]
    [InlineData("activity log", SettingsPaths.LogLevel)]
    [InlineData("androidSdkPath", SettingsPaths.AndroidSdkPath)]
    public void SearchFindsSettingsByNameDescriptionOrPath(string query, string expectedPath)
    {
        var results = SettingsCatalog.Default.Search(new SettingsSearchQuery(query));

        Assert.Contains(results, entry => entry.Path == expectedPath);
    }

    [Fact]
    public void SearchRanksNameMatchesBeforeDescriptionMatches()
    {
        var results = SettingsCatalog.Default.Search(new SettingsSearchQuery("theme"));

        Assert.NotEmpty(results);
        Assert.Equal(SettingsPaths.Theme, results[0].Path);
    }

    [Fact]
    public async Task SearchMatchesTheCurrentValueOnlyWhenASnapshotIsSupplied()
    {
        using var temp = new TempDirectory();
        using var service = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);

        var withoutSnapshot = SettingsCatalog.Default.Search(new SettingsSearchQuery("dark"));
        var withSnapshot = SettingsCatalog.Default.Search(new SettingsSearchQuery("dark"), service.Current);

        Assert.DoesNotContain(withoutSnapshot, entry => entry.Path == SettingsPaths.Theme);
        Assert.Contains(withSnapshot, entry => entry.Path == SettingsPaths.Theme);
    }

    [Fact]
    public void CategoryFilterKeepsOnlyThatSection()
    {
        var results = SettingsCatalog.Default.Search(new SettingsSearchQuery(Category: "performance"));

        Assert.NotEmpty(results);
        Assert.All(results, entry => Assert.StartsWith("performance.", entry.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BasicLevelHidesAdvancedAndExpertSettings()
    {
        var basic = SettingsCatalog.Default.Search(
            new SettingsSearchQuery("adb", MaxLevel: SettingsLevel.Basic));

        Assert.DoesNotContain(basic, entry => entry.Path == SettingsPaths.AdbPath);
        Assert.Contains(
            SettingsCatalog.Default.Entries,
            entry => entry.Path == SettingsPaths.AdbPath && entry.Level == SettingsLevel.Advanced);
    }

    [Fact]
    public void CatalogCoversEveryDefaultLeaf()
    {
        var defaults = SettingsDefaults.CreateCopy();
        var leaves = new List<string>();
        CollectLeaves(defaults, string.Empty, leaves);

        foreach (var leaf in leaves.Where(path => path != "schemaVersion"))
        {
            Assert.True(
                SettingsCatalog.Default.Find(leaf) is not null,
                $"The settings catalog has no entry for '{leaf}'.");
        }
    }

    [Fact]
    public void EveryEntryHasALabelDescriptionAndKnownSection()
    {
        Assert.All(SettingsCatalog.Default.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Label));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.Contains(entry.Category, SettingsCatalog.Default.Categories);
            Assert.StartsWith(entry.Section + ".", entry.Path, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void FindIsCaseInsensitiveAndReturnsNullForUnknownPaths()
    {
        Assert.NotNull(SettingsCatalog.Default.Find("APPEARANCE.THEME"));
        Assert.Null(SettingsCatalog.Default.Find("appearance.unknownSetting"));
    }

    private static void CollectLeaves(JsonNode? node, string prefix, List<string> leaves)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var property in obj)
            {
                CollectLeaves(
                    property.Value,
                    prefix.Length == 0 ? property.Key : prefix + "." + property.Key,
                    leaves);
            }

            return;
        }

        if (prefix.Length > 0)
        {
            leaves.Add(prefix);
        }
    }
}
