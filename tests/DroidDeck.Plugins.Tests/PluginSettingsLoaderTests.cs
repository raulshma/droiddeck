using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Verifies the loader seam: a plugin that ships a readable settings.schema.json
/// gets the schema-driven settings store, and a plugin with an unusable schema
/// keeps the raw store and loads with a warning.
/// </summary>
public sealed class PluginSettingsLoaderTests
{
    private const string PluginId = "com.example.sample";

    [Fact]
    public async Task SchemaPluginGetsTheSchemaDrivenStore()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), """
            { "version": 1, "properties": { "endpoint": { "type": "string", "default": "https://example.com" } } }
            """);
        using var secrets = CreateSecretStore(temp);
        await using var loader = CreateLoader(temp, secrets);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        var settings = Assert.IsType<SchemaPluginSettings>(result.Plugin!.Settings);
        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
        Assert.DoesNotContain(
            result.Validation.Issues,
            issue => issue.Code.StartsWith("plugin.settingsSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidSchemaKeepsTheRawStoreAndLoads() 
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), """
            { "properties": { "value": { "type": "string", "oneOf": [] } } }
            """);
        using var secrets = CreateSecretStore(temp);
        await using var loader = CreateLoader(temp, secrets);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        Assert.IsType<JsonPluginSettings>(result.Plugin!.Settings);
        Assert.Contains(
            result.Validation.Issues,
            issue => issue.Code == "plugin.settingsSchema.invalid" &&
                issue.Severity == PluginValidationSeverity.Warning);
    }

    [Fact]
    public async Task PluginWithoutSchemaKeepsTheRawStoreAndLoadsCleanly()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        await using var loader = CreateLoader(temp, secretStore: null);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        Assert.IsType<JsonPluginSettings>(result.Plugin!.Settings);
        Assert.DoesNotContain(
            result.Validation.Issues,
            issue => issue.Code.StartsWith("plugin.settingsSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecretSchemaWithoutSecretStoreFallsBackToTheRawStoreWithAWarning()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), """
            { "properties": { "apiKey": { "type": "string", "format": "secret" } } }
            """);
        await using var loader = CreateLoader(temp, secretStore: null);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        Assert.IsType<JsonPluginSettings>(result.Plugin!.Settings);
        Assert.Contains(
            result.Validation.Issues,
            issue => issue.Code == "plugin.settingsSchema.noSecretStore" &&
                issue.Severity == PluginValidationSeverity.Warning);
    }

    [Fact]
    public async Task PluginWritesThroughTheSchemaStoreWhenTheSampleUsesSettings()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), """
            { "properties": { "initialized": { "type": "string" } } }
            """);
        using var secrets = CreateSecretStore(temp);
        await using var loader = CreateLoader(temp, secrets);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        Assert.Equal("true", await result.Plugin!.Settings.GetAsync("initialized"));
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")))!;
        Assert.Equal("true", document["values"]!["initialized"]!.GetValue<string>());
    }

    private static PluginLoader CreateLoader(TempDirectory temp, ISecretStore? secretStore)
        => new(
            new PluginRegistry(),
            NullLoggerFactory.Instance,
            hostVersion: "1.0.0",
            secretStore: secretStore);

    private static DpapiSecretStore CreateSecretStore(TempDirectory temp)
        => new(new AppPaths(temp.Combine("secrets-data")));

    private static string StagePlugin(TempDirectory temp)
    {
        var directory = temp.CreateDirectory("plugins", PluginId);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "SampleCommandPlugin.dll"),
            Path.Combine(directory, "SampleCommandPlugin.dll"),
            overwrite: true);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(
                new PluginManifest
                {
                    Id = PluginId,
                    Name = "Sample Plugin",
                    Version = "1.0.0",
                    ApiVersion = "1.0",
                    MinHostVersion = "0.1.0",
                    EntryAssembly = "SampleCommandPlugin.dll",
                    EntryType = "SampleCommandPlugin.SamplePlugin",
                    Capabilities = ["command"],
                },
                PluginJsonContext.Default.PluginManifest));
        return directory;
    }

    private static string Describe(PluginValidationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString()));
}
