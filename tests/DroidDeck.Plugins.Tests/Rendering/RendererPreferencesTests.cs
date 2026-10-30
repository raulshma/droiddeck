using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugins.Rendering;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class RendererPreferencesTests
{
    [Fact]
    public async Task MissingFileYieldsNoDefaults()
    {
        using var temp = new TempDirectory();

        var preferences = await RendererPreferences.LoadAsync(temp.Path);

        Assert.Empty(preferences.Defaults);
        Assert.Null(preferences.GetDefault(RendererKind.Chart));
    }

    [Fact]
    public async Task DefaultsRoundTripAcrossLoads()
    {
        using var temp = new TempDirectory();
        var preferences = await RendererPreferences.LoadAsync(temp.Path);

        await preferences.SetDefaultAsync(RendererKind.Chart, "com.example.chart");
        await preferences.SetDefaultAsync(RendererKind.LogDetail, "com.example.log");

        var reloaded = await RendererPreferences.LoadAsync(temp.Path);

        Assert.Equal("com.example.chart", reloaded.GetDefault(RendererKind.Chart));
        Assert.Equal("com.example.log", reloaded.GetDefault(RendererKind.LogDetail));
        Assert.Null(reloaded.GetDefault(RendererKind.DataView));
    }

    [Fact]
    public async Task ChangingOneKindKeepsTheOthers()
    {
        using var temp = new TempDirectory();
        var preferences = await RendererPreferences.LoadAsync(temp.Path);
        await preferences.SetDefaultAsync(RendererKind.Chart, "com.example.chart");
        await preferences.SetDefaultAsync(RendererKind.LogDetail, "com.example.log");

        await preferences.SetDefaultAsync(RendererKind.Chart, "com.example.chart2");

        var reloaded = await RendererPreferences.LoadAsync(temp.Path);
        Assert.Equal("com.example.chart2", reloaded.GetDefault(RendererKind.Chart));
        Assert.Equal("com.example.log", reloaded.GetDefault(RendererKind.LogDetail));
    }

    [Fact]
    public async Task ClearingAKindRemovesOnlyThatPreference()
    {
        using var temp = new TempDirectory();
        var preferences = await RendererPreferences.LoadAsync(temp.Path);
        await preferences.SetDefaultAsync(RendererKind.Chart, "com.example.chart");
        await preferences.SetDefaultAsync(RendererKind.LogDetail, "com.example.log");

        await preferences.SetDefaultAsync(RendererKind.Chart, null);

        var reloaded = await RendererPreferences.LoadAsync(temp.Path);
        Assert.Null(reloaded.GetDefault(RendererKind.Chart));
        Assert.Equal("com.example.log", reloaded.GetDefault(RendererKind.LogDetail));
    }

    [Fact]
    public async Task ResetClearsEveryPreference()
    {
        using var temp = new TempDirectory();
        var preferences = await RendererPreferences.LoadAsync(temp.Path);
        await preferences.SetDefaultAsync(RendererKind.Chart, "com.example.chart");
        await preferences.SetDefaultAsync(RendererKind.LogDetail, "com.example.log");

        await preferences.ResetAsync();

        var reloaded = await RendererPreferences.LoadAsync(temp.Path);
        Assert.Empty(reloaded.Defaults);
    }

    [Fact]
    public async Task ACorruptFileYieldsDefaults()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(RendererPreferences.FileName, "{ not json");

        var preferences = await RendererPreferences.LoadAsync(temp.Path);

        Assert.Empty(preferences.Defaults);
    }

    [Fact]
    public async Task UnknownKindsAndBlankValuesAreIgnored()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            RendererPreferences.FileName,
            """
            {
              "schemaVersion": 2,
              "defaults": {
                "Chart": "com.example.chart",
                "NotAKind": "com.example.other",
                "LogDetail": "  "
              }
            }
            """);

        var preferences = await RendererPreferences.LoadAsync(temp.Path);

        Assert.Equal("com.example.chart", preferences.GetDefault(RendererKind.Chart));
        Assert.Null(preferences.GetDefault(RendererKind.LogDetail));
    }
}
