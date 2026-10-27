using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class JsonPluginSettingsTests
{
    [Fact]
    public async Task SetAndGetRoundTripPersistsToDisk()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("settings.json");
        using var settings = new JsonPluginSettings(path);

        await settings.SetAsync("theme", "dark");
        await settings.SetAsync("refreshSeconds", "30");

        Assert.Equal("dark", await settings.GetAsync("theme"));
        Assert.Equal("30", await settings.GetAsync("refreshSeconds"));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"theme\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refreshSeconds\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValuesSurviveReload()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("settings.json");
        using (var settings = new JsonPluginSettings(path))
        {
            await settings.SetAsync("endpoint", "https://example.com");
        }

        using var reloaded = new JsonPluginSettings(path);
        Assert.Equal("https://example.com", await reloaded.GetAsync("endpoint"));
    }

    [Fact]
    public async Task RemoveDeletesTheKeyAndPersists()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("settings.json");
        using var settings = new JsonPluginSettings(path);
        await settings.SetAsync("obsolete", "value");

        await settings.RemoveAsync("obsolete");

        Assert.Null(await settings.GetAsync("obsolete"));
        using var reloaded = new JsonPluginSettings(path);
        Assert.Null(await reloaded.GetAsync("obsolete"));
    }

    [Fact]
    public async Task SettingNullRemovesTheKey()
    {
        using var temp = new TempDirectory();
        using var settings = new JsonPluginSettings(temp.Combine("settings.json"));
        await settings.SetAsync("key", "value");

        await settings.SetAsync("key", null);

        Assert.Null(await settings.GetAsync("key"));
    }

    [Fact]
    public async Task MissingKeyReturnsNull()
    {
        using var temp = new TempDirectory();
        using var settings = new JsonPluginSettings(temp.Combine("settings.json"));

        Assert.Null(await settings.GetAsync("absent"));
    }

    [Fact]
    public async Task UnicodeKeysAndValuesRoundTrip()
    {
        using var temp = new TempDirectory();
        using var settings = new JsonPluginSettings(temp.Combine("settings.json"));

        await settings.SetAsync("ключ", "значение ✓");

        Assert.Equal("значение ✓", await settings.GetAsync("ключ"));
    }

    [Fact]
    public async Task CorruptFileIsQuarantinedAndSettingsContinueEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("settings.json", "{ this is not json");
        using var settings = new JsonPluginSettings(path);

        Assert.Null(await settings.GetAsync("key"));
        await settings.SetAsync("key", "recovered");

        Assert.Equal("recovered", await settings.GetAsync("key"));
        var quarantineDirectory = temp.Combine("quarantine");
        Assert.True(Directory.Exists(quarantineDirectory));
        Assert.Single(Directory.EnumerateFiles(quarantineDirectory, "*.corrupt"));
    }

    [Fact]
    public async Task BlankKeysAreRejected()
    {
        using var temp = new TempDirectory();
        using var settings = new JsonPluginSettings(temp.Combine("settings.json"));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.GetAsync(" "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.SetAsync("", "value"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => settings.RemoveAsync(null!));
    }

    [Fact]
    public async Task DisposedSettingsThrow()
    {
        using var temp = new TempDirectory();
        var settings = new JsonPluginSettings(temp.Combine("settings.json"));
        settings.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => settings.GetAsync("key"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => settings.SetAsync("key", "value"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => settings.RemoveAsync("key"));
        settings.Dispose();
    }
}
