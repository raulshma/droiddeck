using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class SettingsTransferTests
{
    [Fact]
    public void ValidationReportsTheJsonPathOfATypeError()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);

        var result = service.ValidateSettings("""{ "screen": { "maxSize": "wide" } }""");

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues, issue => issue.Path == SettingsPaths.ScreenMaxSize);
        Assert.Equal(SettingsIssueSeverity.Error, issue.Severity);
        Assert.Contains("maxSize", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationAcceptsCommentsTrailingCommasAndReportsUnknownKeys()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);

        var result = service.ValidateSettings(
            """
            {
              // A forward-compatible section written by a newer build.
              "appearance": { "theme": "dark", },
              "future": { "flag": true }
            }
            """);

        Assert.True(result.IsValid);
        Assert.Contains(
            result.Issues,
            issue => issue.Path == "future.flag" && issue.Severity == SettingsIssueSeverity.Warning);
    }

    [Fact]
    public void ValidationRejectsNewerSchemaVersionsAndDuplicateCaseKeys()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);

        var newer = service.ValidateSettings("""{ "schemaVersion": 99 }""");
        var duplicate = service.ValidateSettings("""{ "screen": { "maxSize": 10, "MaxSize": 20 } }""");

        Assert.False(newer.IsValid);
        Assert.Contains(newer.Issues, issue => issue.Path == "schemaVersion" && issue.Severity == SettingsIssueSeverity.Error);
        Assert.False(duplicate.IsValid);
        Assert.Contains(duplicate.Issues, issue => issue.Path == SettingsPaths.ScreenMaxSize);
    }

    [Fact]
    public async Task ReplacePreviewListsAddedChangedAndRemovedValues()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync(SettingsPaths.Density, "compact");
        await service.SetAsync(SettingsPaths.LogLevel, "warning");

        var preview = await service.PreviewSettingsImportAsync(
            """{ "appearance": { "theme": "light" }, "logging": { "level": "debug" } }""",
            ConfigScope.Global,
            SettingsImportMode.Replace);

        Assert.True(preview.Validation.IsValid);
        Assert.True(preview.HasChanges);
        Assert.Contains(
            preview.Changes,
            change => change.Path == SettingsPaths.Theme &&
                      change.Kind == SettingsChangeKind.Changed &&
                      change.OldValue!.GetValue<string>() == "dark" &&
                      change.NewValue!.GetValue<string>() == "light");
        Assert.Contains(
            preview.Changes,
            change => change.Path == SettingsPaths.LogLevel && change.Kind == SettingsChangeKind.Changed);
        Assert.Contains(
            preview.Changes,
            change => change.Path == SettingsPaths.Density && change.Kind == SettingsChangeKind.Removed);
    }

    [Fact]
    public async Task MergePreviewOnlyAddsTheImportedLeaves()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);

        var preview = await service.PreviewSettingsImportAsync(
            """{ "performance": { "maxSamples": 120 } }""",
            ConfigScope.Global,
            SettingsImportMode.Merge);

        Assert.Contains(
            preview.Changes,
            change => change.Path == SettingsPaths.PerformanceMaxSamples &&
                      change.Kind == SettingsChangeKind.Added &&
                      change.OldValue is null &&
                      change.NewValue!.GetValue<int>() == 120);
        Assert.DoesNotContain(preview.Changes, change => change.Kind == SettingsChangeKind.Removed);
    }

    [Fact]
    public async Task ImportAppliesChangesAndKeepsABackup()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);

        var result = await service.ImportSettingsAsync(
            """{ "appearance": { "theme": "light" }, "performance": { "maxSamples": 120 } }""",
            ConfigScope.Global,
            SettingsImportMode.Merge);

        Assert.Equal(AppTheme.Light, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal(120, service.GetValue<int>(SettingsPaths.PerformanceMaxSamples));
        Assert.Equal(2, result.Preview.Changes.Count);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
    }

    [Fact]
    public async Task InvalidImportIsRejectedWithoutWriting()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.ScreenMaxSize, 720);

        var exception = await Assert.ThrowsAsync<SettingsValidationException>(
            () => service.ImportSettingsAsync("""{ "screen": { "maxSize": "wide" } }"""));

        Assert.False(exception.Result.IsValid);
        Assert.Equal(720, service.GetValue<int>(SettingsPaths.ScreenMaxSize));
    }

    [Fact]
    public async Task ExportOmitsSecretsAndCanBeImportedBack()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync("custom.apiToken", "super-secret");

        var export = await service.ExportSettingsAsync(new SettingsExportOptions { Scopes = [ConfigScope.Global] });
        var json = export.ToJson();

        Assert.DoesNotContain("super-secret", json, StringComparison.Ordinal);
        Assert.Contains("custom.apiToken", export.ExcludedPaths);
        Assert.Equal("dark", export.Scopes[ConfigScope.Global]["appearance"]!["theme"]!.GetValue<string>());

        await service.ResetAsync(SettingsPaths.Theme);
        var result = await service.ImportSettingsAsync(json, ConfigScope.Global, SettingsImportMode.Replace);

        Assert.Equal(AppTheme.Dark, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Contains(result.Preview.Changes, change => change.Path == SettingsPaths.Theme);
    }

    [Fact]
    public async Task ImportDropsSecretKeysInsteadOfPersistingThem()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        var result = await service.ImportSettingsAsync(
            """{ "appearance": { "theme": "dark" }, "custom": { "apiToken": "abc" } }""");

        Assert.Contains("custom.apiToken", result.Preview.ExcludedPaths);
        Assert.Null(service.GetValue<string>("custom.apiToken"));
        Assert.Equal(AppTheme.Dark, service.GetValue<AppTheme>(SettingsPaths.Theme));
    }

    [Fact]
    public async Task ResetSectionClearsOnlyThatSectionAndUndoRestoresIt()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync(SettingsPaths.Density, "compact");
        await service.SetAsync(SettingsPaths.LogLevel, "debug");

        var snapshot = await service.ResetSectionAsync("appearance");

        Assert.Equal(AppTheme.System, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal("comfortable", service.GetString(SettingsPaths.Density));
        Assert.Equal("debug", service.GetString(SettingsPaths.LogLevel));
        Assert.Contains(
            snapshot.Removed,
            change => change.Path == SettingsPaths.Theme &&
                      change.Kind == SettingsChangeKind.Removed &&
                      change.OldValue!.GetValue<string>() == "dark");
        Assert.DoesNotContain(snapshot.Removed, change => change.Path == SettingsPaths.LogLevel);

        await service.RestoreSettingsAsync(snapshot);

        Assert.Equal(AppTheme.Dark, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal("compact", service.GetString(SettingsPaths.Density));
    }

    [Fact]
    public async Task ResetScopeClearsEveryOverrideInThatScope()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Light, ConfigScope.Session);

        var snapshot = await service.ResetScopeAsync(ConfigScope.Global);

        Assert.Equal(AppTheme.Light, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal("dark", snapshot.Document["appearance"]!["theme"]!.GetValue<string>());

        await service.RestoreSettingsAsync(snapshot);
        await service.ResetAsync(SettingsPaths.Theme, ConfigScope.Session);

        Assert.Equal(AppTheme.Dark, service.GetValue<AppTheme>(SettingsPaths.Theme));
    }

    [Fact]
    public async Task ScopeValuesExposeExplicitOverridesPerLayer()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Light, ConfigScope.Session);

        Assert.Equal("dark", service.GetValueAt(ConfigScope.Global, SettingsPaths.Theme)!.GetValue<string>());
        Assert.Equal("light", service.GetValueAt(ConfigScope.Session, SettingsPaths.Theme)!.GetValue<string>());
        Assert.Null(service.GetValueAt(ConfigScope.Global, SettingsPaths.PerformanceMaxSamples));
    }

    private static ConfigurationService CreateService(TempDirectory temp) => new(new AppPaths(temp.Combine("data")));
}
