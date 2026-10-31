using System.Text.Json.Nodes;
using DroidDeck.Core;
using DroidDeck.Core.Security;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Verifies that one factory owns the schema stores of its plugins: plugins
/// with identical setting names stay isolated, a plugin's store is shared
/// between the loader and host surfaces, and resetting a value restores the
/// schema default.
/// </summary>
public sealed class PluginSettingsFactoryTests : IDisposable
{
    private const string Schema = """
        { "version": 1, "properties": { "endpoint": { "type": "string", "default": "https://example.com" } } }
        """;

    private readonly TempDirectory _temp = new();
    private readonly DpapiSecretStore _secrets;

    public PluginSettingsFactoryTests()
    {
        _secrets = new DpapiSecretStore(new AppPaths(_temp.Combine("secrets-data")));
    }

    public void Dispose()
    {
        _secrets.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task TwoPluginsWithTheSameSettingNamesGetIsolatedStores()
    {
        var firstDirectory = await CreatePluginAsync("com.example.first");
        var secondDirectory = await CreatePluginAsync("com.example.second");
        using var factory = new PluginSettingsFactory(_secrets);

        var first = await factory.CreateForInstalledPluginAsync("com.example.first", firstDirectory);
        var second = await factory.CreateForInstalledPluginAsync("com.example.second", secondDirectory);

        Assert.True(first.UsesSchema);
        Assert.True(second.UsesSchema);
        Assert.NotSame(first.Settings, second.Settings);

        await first.SchemaSettings!.SetAsync("endpoint", "https://first.example");
        await second.SchemaSettings!.SetAsync("endpoint", "https://second.example");

        Assert.Equal("https://first.example", await first.SchemaSettings.GetAsync("endpoint"));
        Assert.Equal("https://second.example", await second.SchemaSettings.GetAsync("endpoint"));
        Assert.Equal(
            "https://first.example",
            ReadStoredValue(firstDirectory, "endpoint"));
        Assert.Equal(
            "https://second.example",
            ReadStoredValue(secondDirectory, "endpoint"));
    }

    [Fact]
    public async Task CreateForInstalledPluginReturnsTheOneStorePerSettingsFile()
    {
        var directory = await CreatePluginAsync("com.example.shared");
        using var factory = new PluginSettingsFactory(_secrets);

        var first = await factory.CreateForInstalledPluginAsync("com.example.shared", directory);
        var second = await factory.CreateForInstalledPluginAsync("com.example.shared", directory);

        Assert.Same(first.Settings, second.Settings);
        Assert.Same(first.SchemaSettings, second.SchemaSettings);
    }

    [Fact]
    public async Task SeparateFactoriesDoNotShareAStore()
    {
        var directory = await CreatePluginAsync("com.example.isolated");
        using var firstFactory = new PluginSettingsFactory(_secrets);
        using var secondFactory = new PluginSettingsFactory(_secrets);

        var first = await firstFactory.CreateForInstalledPluginAsync("com.example.isolated", directory);
        var second = await secondFactory.CreateForInstalledPluginAsync("com.example.isolated", directory);

        Assert.NotSame(first.Settings, second.Settings);
    }

    [Fact]
    public async Task RemovingAValueRestoresTheSchemaDefault()
    {
        var directory = await CreatePluginAsync("com.example.defaults");
        using var factory = new PluginSettingsFactory(_secrets);
        var creation = await factory.CreateForInstalledPluginAsync("com.example.defaults", directory);
        var settings = creation.SchemaSettings!;
        await settings.SetAsync("endpoint", "https://changed.example");

        await settings.RemoveAsync("endpoint");

        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
        Assert.Equal("https://example.com", (await settings.GetNodeAsync("endpoint"))!.GetValue<string>());
        var document = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(directory, "settings.json")))!;
        Assert.Empty((JsonObject)document["values"]!);
    }

    [Fact]
    public async Task PluginWithoutSchemaGetsTheRawStore()
    {
        var directory = _temp.CreateDirectory("plugins", "com.example.raw");
        using var factory = new PluginSettingsFactory(_secrets);

        var creation = await factory.CreateForInstalledPluginAsync("com.example.raw", directory);

        Assert.False(creation.UsesSchema);
        Assert.IsType<JsonPluginSettings>(creation.Settings);
        Assert.Null(creation.Issue);
    }

    [Fact]
    public async Task DisposingTheFactoryDisposesItsSchemaStores()
    {
        var directory = await CreatePluginAsync("com.example.disposed");
        var factory = new PluginSettingsFactory(_secrets);
        var creation = await factory.CreateForInstalledPluginAsync("com.example.disposed", directory);

        factory.Dispose();

        Assert.True(creation.SchemaSettings!.IsDisposed);
    }

    [Fact]
    public async Task ASchemaWithoutASecretStoreFallsBackToTheRawStore()
    {
        var directory = await CreatePluginAsync("com.example.nosecret");
        using var factory = new PluginSettingsFactory();

        var creation = await factory.CreateForInstalledPluginAsync("com.example.nosecret", directory);

        Assert.False(creation.UsesSchema);
        Assert.IsType<JsonPluginSettings>(creation.Settings);
        Assert.Equal("plugin.settingsSchema.noSecretStore", creation.Issue?.Code);
    }

    private async Task<string> CreatePluginAsync(string pluginId)
    {
        var directory = _temp.CreateDirectory("plugins", pluginId);
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), Schema);
        return directory;
    }

    private static string? ReadStoredValue(string directory, string key)
    {
        var document = (JsonObject)JsonNode.Parse(
            File.ReadAllText(Path.Combine(directory, "settings.json")))!;
        return document["values"] is JsonObject values
            ? values[key]?.GetValue<string>()
            : null;
    }
}
