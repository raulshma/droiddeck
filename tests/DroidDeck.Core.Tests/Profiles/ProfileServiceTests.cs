using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Features;
using DroidDeck.Core.Profiles;
using DroidDeck.Core.Projects;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Profiles;

public sealed class ProfileServiceTests
{
    [Fact]
    public async Task CreateApplyAndReloadProfileUpdatesTheSourceMap()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        using var profiles = new ProfileService(new AppPaths(temp.Combine("data")), configuration);

        var profile = await profiles.CreateAsync(
            "CI",
            new JsonObject { ["appearance"] = new JsonObject { ["density"] = "compact" } });
        await profiles.ApplyAsync(profile.Id);

        Assert.Equal("compact", configuration.GetString(SettingsPaths.Density));
        var source = configuration.GetSource(SettingsPaths.Density);
        Assert.Equal(ConfigScope.Profile, source.Scope);
        Assert.Contains($"profiles/{profile.Id}.json", source.Origin, StringComparison.OrdinalIgnoreCase);

        var file = Path.Combine(temp.Combine("data"), "profiles", profile.Id + ".json");
        await File.WriteAllTextAsync(
            file,
            """{ "appearance": { "density": "comfortable" } }""");
        await configuration.ReloadProfileAsync();

        Assert.Equal("comfortable", configuration.GetString(SettingsPaths.Density));
        Assert.Equal(profile.Id, configuration.GetString(SettingsPaths.ActiveProfileId));
    }

    [Fact]
    public async Task ExportAndImportNeverCarrySecretValues()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        using var profiles = new ProfileService(new AppPaths(temp.Combine("data")), configuration);

        var profile = await profiles.CreateAsync(
            "Work",
            new JsonObject
            {
                ["tools"] = new JsonObject { ["adbPath"] = @"C:\adb.exe" },
                ["plugin"] = new JsonObject { ["apiToken"] = "super-secret" },
            });

        var exported = await profiles.ExportAsync(profile.Id);
        Assert.DoesNotContain("super-secret", exported, StringComparison.Ordinal);
        Assert.Contains("apiToken", exported, StringComparison.Ordinal);

        var imported = await profiles.ImportAsync(exported);
        Assert.NotEqual(profile.Id, imported.Metadata.Id);
        Assert.Contains(imported.ExcludedPaths, path => path.Contains("apiToken", StringComparison.OrdinalIgnoreCase));

        var importedFile = Path.Combine(temp.Combine("data"), "profiles", imported.Metadata.Id + ".json");
        var layer = await File.ReadAllTextAsync(importedFile);
        Assert.DoesNotContain("super-secret", layer, StringComparison.Ordinal);
        Assert.Contains(@"C:\\adb.exe", layer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateDeleteAndActiveProfileSelectionStayConsistent()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        using var profiles = new ProfileService(paths, configuration);

        var original = await profiles.CreateAsync("Device lab");
        var copy = await profiles.DuplicateAsync(original.Id, "Device lab 2");
        await profiles.ApplyAsync(original.Id);
        Assert.Equal(original.Id, profiles.GetActiveProfileId());

        Assert.True(await profiles.DeleteAsync(original.Id));
        Assert.Null(profiles.GetActiveProfileId());
        Assert.Null(configuration.GetString(SettingsPaths.ActiveProfileId));

        var remaining = await profiles.ListAsync();
        Assert.Single(remaining);
        Assert.Equal(copy.Id, remaining[0].Id);
        Assert.False(File.Exists(Path.Combine(paths.ProfilesDirectory, original.Id + ".json")));
    }

    [Fact]
    public async Task ImportReplaceUpdatesAnExistingProfile()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using var configuration = new ConfigurationService(paths);
        using var profiles = new ProfileService(paths, configuration);

        var profile = await profiles.CreateAsync("QA");
        var exported = await profiles.ExportAsync(profile.Id);

        var result = await profiles.ImportAsync(exported, ProfileImportMode.Replace);

        Assert.True(result.Replaced);
        Assert.Equal(profile.Id, result.Metadata.Id);
    }

    [Fact]
    public async Task ImportRejectsANonObjectSettingsMember()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        using var profiles = new ProfileService(new AppPaths(temp.Combine("data")), configuration);

        await Assert.ThrowsAsync<ProfileException>(() =>
            profiles.ImportAsync(
                """{ "profile": { "name": "Broken" }, "settings": [1, 2] }"""));
    }

    [Fact]
    public void PresetsCreateValidConfigurationsForEveryBuiltInStack()
    {
        var project = new ProjectModel
        {
            Name = "Sample",
            Kind = ProjectKind.AndroidGradle,
            ApplicationId = "com.example.sample",
            LauncherActivity = ".MainActivity",
            Modules = [new ProjectModule { Name = "app", Type = ProjectModuleType.Application }],
        };

        foreach (var preset in RunConfigurationPresetLibrary.Default)
        {
            var configuration = RunConfigurationPresetLibrary.Create(preset.Id, project);
            Assert.Equal(preset.Kind, configuration.Kind);
            Assert.False(string.IsNullOrWhiteSpace(configuration.Name));
            Assert.False(string.IsNullOrWhiteSpace(configuration.Artifact.Glob));
        }

        var gradle = RunConfigurationPresetLibrary.Create("gradle-debug", project);
        Assert.Equal(":app:assembleDebug", gradle.Build.Task);
        Assert.Equal("com.example.sample", gradle.Launch.PackageName);
        Assert.Contains(
            RunConfigurationPresetLibrary.Suggest(ProjectKind.Flutter),
            preset => preset.Id == "flutter-debug");
    }

    [Fact]
    public async Task HiddenFeaturesDisappearButKeepTheirConfigurationAndRestore()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        var visibility = new FeatureVisibilityService(configuration);

        await configuration.SetAsync("inspector.maxDepth", 24);
        await visibility.HideAsync("nav.inspector");

        Assert.False(visibility.IsVisible("nav.inspector"));
        Assert.DoesNotContain(visibility.GetVisible(FeatureCategory.Navigation), feature => feature.Id == "nav.inspector");
        Assert.Equal(24, configuration.GetValue<int>("inspector.maxDepth"));
        Assert.Equal(FeatureCapability.Hidden, visibility.GetBadge("nav.inspector").Capability);

        await visibility.ShowAsync("nav.inspector");

        Assert.True(visibility.IsVisible("nav.inspector"));
        Assert.Equal(24, configuration.GetValue<int>("inspector.maxDepth"));
        Assert.Equal(FeatureCapability.Full, visibility.GetBadge("nav.inspector").Capability);
    }

    [Fact]
    public void CapabilityBadgesExplainLimitedAndCustomSupport()
    {
        Assert.Equal(FeatureCapability.Limited, RunConfigurationPresetLibrary.GetCapability(ProjectKind.Flutter));
        Assert.Equal(FeatureCapability.ApkOnly, RunConfigurationPresetLibrary.GetCapability(ProjectKind.ApkOnly));
        Assert.Equal(FeatureCapability.Custom, RunConfigurationPresetLibrary.GetCapability(ProjectKind.Custom));

        var limited = CapabilityBadges.For(FeatureCapability.Limited);
        Assert.Equal("Limited support", limited.Title);
        Assert.False(string.IsNullOrWhiteSpace(limited.Description));
        Assert.Equal(CapabilityBadgeTone.Warning, limited.Tone);
    }
}
