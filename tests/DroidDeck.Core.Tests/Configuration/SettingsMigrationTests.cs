using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class SettingsMigrationTests
{
    [Fact]
    public void DefaultMigratorUpgradesLegacyUnversionedDocuments()
    {
        var document = new JsonObject { ["appearance"] = new JsonObject { ["theme"] = "dark" } };

        var version = SettingsMigrator.Default.Migrate(document, fromVersion: 0);

        Assert.Equal(AppSettings.CurrentSchemaVersion, version);
        Assert.Equal(AppSettings.CurrentSchemaVersion, document["schemaVersion"]!.GetValue<int>());
        Assert.True(document["onboarding"]!["completed"]!.GetValue<bool>());
        Assert.Equal("dark", document["appearance"]!["theme"]!.GetValue<string>());
    }

    [Fact]
    public void CurrentVersionDocumentsAreLeftAlone()
    {
        var document = new JsonObject { ["schemaVersion"] = 1, ["appearance"] = new JsonObject { ["theme"] = "dark" } };

        var version = SettingsMigrator.Default.Migrate(document, fromVersion: 1);

        Assert.Equal(1, version);
        Assert.Null(document["onboarding"]);
    }

    [Fact]
    public void ChainedMigrationsRunInOrderAndPreserveUnknownKeys()
    {
        var steps = new Dictionary<int, Action<JsonObject>>
        {
            [0] = document => document["first"] = true,
            [1] = document => document["second"] = true,
        };
        var migrator = new SettingsMigrator(steps: steps, targetVersion: 2);
        var document = new JsonObject { ["experimental"] = new JsonObject { ["flag"] = true } };

        var version = migrator.Migrate(document, fromVersion: 0);

        Assert.Equal(2, version);
        Assert.True(document["first"]!.GetValue<bool>());
        Assert.True(document["second"]!.GetValue<bool>());
        Assert.True(document["experimental"]!["flag"]!.GetValue<bool>());
        Assert.Equal(2, document["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void CustomStepsCanReplaceBuiltInSteps()
    {
        var steps = new Dictionary<int, Action<JsonObject>>
        {
            [0] = document => document["custom"] = "migrated",
        };
        var migrator = new SettingsMigrator(steps: steps);
        var document = new JsonObject();

        migrator.Migrate(document, fromVersion: 0);

        Assert.Equal("migrated", document["custom"]!.GetValue<string>());
        Assert.Null(document["onboarding"]);
    }

    [Fact]
    public async Task ServiceMigratesPersistedDocumentsOnLoad()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "appearance": { "theme": "dark" } }""");
        using var service = new ConfigurationService(paths);

        await service.InitializeAsync();

        Assert.Equal(AppTheme.Dark, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.True(service.GetValue<bool>("onboarding.completed"));
        Assert.Equal(ConfigScope.Global, service.GetSource("onboarding.completed").Scope);
    }

    [Fact]
    public async Task ServiceAppliesCustomMigrationSteps()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "appearance": { "theme": "dark" } }""");
        var steps = new Dictionary<int, Action<JsonObject>>
        {
            [0] = document => document["custom"] = "migrated",
        };
        using var service = new ConfigurationService(paths, migrator: new SettingsMigrator(steps: steps));

        await service.InitializeAsync();

        Assert.Equal("migrated", service.GetString("custom"));
        Assert.Equal(ConfigScope.Global, service.GetSource("custom").Scope);
    }

    [Fact]
    public async Task MigratedDocumentsStampTheNewVersionOnNextSave()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """{ "appearance": { "theme": "dark" } }""");
        using var service = new ConfigurationService(paths);
        await service.InitializeAsync();

        await service.SetAsync(SettingsPaths.Density, "compact");

        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.Equal(
            AppSettings.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }
}
