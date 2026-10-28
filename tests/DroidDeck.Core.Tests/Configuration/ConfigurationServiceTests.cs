using System.Diagnostics;
using System.Text.Json;
using DroidDeck.Core.Configuration;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class ConfigurationServiceTests
{
    [Fact]
    public void DefaultsAreEffectiveBeforeInitialization()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);

        Assert.False(service.IsInitialized);
        Assert.Equal(AppTheme.System, service.Current.Settings.Appearance.Theme);
        Assert.Null(service.Current.Settings.Tools.AdbPath);
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(SettingsPaths.Theme).Scope);
        Assert.False(service.Current.IsOverridden(SettingsPaths.Theme));
        Assert.Null(service.LastLoadNotice);
    }

    [Fact]
    public async Task InitializeUsesDefaultsWhenNoFileExists()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.Null(service.LastLoadNotice);
        Assert.Equal(AppTheme.System, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal(20_000, service.GetValue<int>("logging.maxUiLines"));
        Assert.Equal("comfortable", service.GetString(SettingsPaths.Density));
        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.False(File.Exists(Paths(temp).SettingsFile));
    }

    [Fact]
    public async Task InitializationCreatesTheDataDirectoryTree()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.DiagnosticsDirectory));
        Assert.True(Directory.Exists(paths.DevicesDirectory));
        Assert.True(Directory.Exists(paths.SessionsDirectory));
        Assert.True(Directory.Exists(paths.PluginsDirectory));
        Assert.True(Directory.Exists(paths.QuarantineDirectory));
    }

    [Fact]
    public async Task InitializationCompletesWithinBudget()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        var stopwatch = Stopwatch.StartNew();

        await service.InitializeAsync();

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Initialization took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task SettingsPersistAcrossRestart()
    {
        using var temp = new TempDirectory();
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
            await service.SetAsync(SettingsPaths.Density, "compact");
            await service.SetAsync("logging.maxUiLines", 5000);
        }

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();

        Assert.Equal(AppTheme.Dark, restarted.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal(AppTheme.Dark, restarted.Current.Settings.Appearance.Theme);
        Assert.Equal("compact", restarted.GetString(SettingsPaths.Density));
        Assert.Equal(5000, restarted.GetValue<int>("logging.maxUiLines"));
        Assert.Equal(ConfigScope.Global, restarted.GetSource(SettingsPaths.Theme).Scope);
        Assert.Equal(Paths(temp).SettingsFile, restarted.GetSource(SettingsPaths.Theme).Origin);
    }

    [Fact]
    public void OnboardingDefaultsToIncompleteFirstStep()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);

        Assert.False(service.Current.Settings.Onboarding.Completed);
        Assert.Equal("tools", service.Current.Settings.Onboarding.Step);
        Assert.Equal(
            ConfigScope.BuiltInDefaults,
            service.GetSource(SettingsPaths.OnboardingCompleted).Scope);
        Assert.Equal(
            ConfigScope.BuiltInDefaults,
            service.GetSource(SettingsPaths.OnboardingStep).Scope);
    }

    [Fact]
    public async Task OnboardingStatePersistsAcrossRestartAndResetsToDefaults()
    {
        using var temp = new TempDirectory();
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await service.SetAsync(SettingsPaths.OnboardingStep, "device");
            await service.SetAsync(SettingsPaths.OnboardingCompleted, true);
        }

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();

        Assert.True(restarted.Current.Settings.Onboarding.Completed);
        Assert.Equal("device", restarted.Current.Settings.Onboarding.Step);

        await restarted.ResetAsync(SettingsPaths.OnboardingCompleted);
        await restarted.ResetAsync(SettingsPaths.OnboardingStep);

        Assert.False(restarted.Current.Settings.Onboarding.Completed);
        Assert.Equal("tools", restarted.Current.Settings.Onboarding.Step);
        Assert.Equal(
            ConfigScope.BuiltInDefaults,
            restarted.GetSource(SettingsPaths.OnboardingCompleted).Scope);
    }

    [Fact]
    public async Task LegacySettingsWithoutOnboardingAreMarkedComplete()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("data"));
        await File.WriteAllTextAsync(
            Paths(temp).SettingsFile,
            """{ "schemaVersion": 1, "tools": { "adbPath": "C:\\adb\\adb.exe" } }""");

        using var service = CreateService(temp);
        await service.InitializeAsync();

        Assert.True(service.Current.Settings.Onboarding.Completed);
        Assert.Equal(ConfigScope.Global, service.GetSource(SettingsPaths.OnboardingCompleted).Scope);

        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();
        Assert.True(restarted.Current.Settings.Onboarding.Completed);
    }

    [Fact]
    public async Task ExplicitOnboardingIncompleteIsNotMigrated()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Combine("data"));
        await File.WriteAllTextAsync(
            Paths(temp).SettingsFile,
            """{ "schemaVersion": 1, "onboarding": { "completed": false } }""");

        using var service = CreateService(temp);
        await service.InitializeAsync();

        Assert.False(service.Current.Settings.Onboarding.Completed);
    }

    [Fact]
    public async Task SetPersistsSchemaVersionAndRaisesChanged()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        ConfigChangedEventArgs? change = null;
        service.Changed += (_, args) => change = args;

        await service.SetAsync(SettingsPaths.Theme, AppTheme.Light);

        Assert.NotNull(change);
        Assert.Equal(SettingsPaths.Theme, change.Path);
        Assert.Equal(ConfigScope.Global, change.Scope);
        Assert.Equal(ConfigChangeKind.Set, change.Kind);
        Assert.Equal(ConfigScope.Global, change.Source.Scope);
        using var document = await ReadDocumentAsync(Paths(temp).SettingsFile);
        Assert.Equal(
            AppSettings.CurrentSchemaVersion,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task ResetReturnsToDefaultsAndRaisesChanged()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Density, "compact");
        ConfigChangedEventArgs? change = null;
        service.Changed += (_, args) => change = args;

        await service.ResetAsync(SettingsPaths.Density);

        Assert.Equal("comfortable", service.GetString(SettingsPaths.Density));
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(SettingsPaths.Density).Scope);
        Assert.NotNull(change);
        Assert.Equal(ConfigChangeKind.Reset, change.Kind);
        Assert.False(service.Current.IsOverridden(SettingsPaths.Density));
    }

    [Fact]
    public async Task WritesAreAtomicAndLeaveARestorableBackup()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        using var service = CreateService(temp);
        await service.InitializeAsync();

        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Light);

        Assert.True(File.Exists(paths.SettingsFile));
        Assert.True(File.Exists(paths.SettingsBackupFile));
        using var current = await ReadDocumentAsync(paths.SettingsFile);
        Assert.Equal("light", current.RootElement.GetProperty("appearance").GetProperty("theme").GetString());
        using var backup = await ReadDocumentAsync(paths.SettingsBackupFile);
        Assert.Equal("dark", backup.RootElement.GetProperty("appearance").GetProperty("theme").GetString());
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptSettingsAreQuarantinedAndDefaultsAreUsed()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, "{ this is not json");
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.NotNull(service.LastLoadNotice);
        Assert.False(service.LastLoadNotice.RecoveredFromBackup);
        Assert.NotNull(service.LastLoadNotice.QuarantinedPath);
        Assert.True(File.Exists(service.LastLoadNotice.QuarantinedPath));
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.Equal(AppTheme.System, service.Current.Settings.Appearance.Theme);
    }

    [Fact]
    public async Task CorruptSettingsAreRestoredFromTheBackup()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
            await service.SetAsync(SettingsPaths.Density, "compact");
        }

        await File.WriteAllTextAsync(paths.SettingsFile, "corrupt");
        using var restarted = CreateService(temp);

        await restarted.InitializeAsync();

        Assert.NotNull(restarted.LastLoadNotice);
        Assert.True(restarted.LastLoadNotice.RecoveredFromBackup);
        Assert.NotNull(restarted.LastLoadNotice.QuarantinedPath);
        Assert.Equal(AppTheme.Dark, restarted.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal(ConfigScope.Global, restarted.GetSource(SettingsPaths.Theme).Scope);
        Assert.True(File.Exists(paths.SettingsFile));
        using var restored = await ReadDocumentAsync(paths.SettingsFile);
        Assert.Equal("dark", restored.RootElement.GetProperty("appearance").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task CorruptBackupIsQuarantinedAsWell()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, "corrupt");
        await File.WriteAllTextAsync(paths.SettingsBackupFile, "also corrupt");
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.NotNull(service.LastLoadNotice);
        Assert.False(service.LastLoadNotice.RecoveredFromBackup);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.SettingsBackupFile));
        Assert.Equal(2, Directory.EnumerateFiles(paths.QuarantineDirectory, "*.corrupt").Count());
        Assert.Equal(AppTheme.System, service.Current.Settings.Appearance.Theme);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2 }""")]
    [InlineData("""{ "schemaVersion": "one" }""")]
    [InlineData("""{ "schemaVersion": 0 }""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public async Task UnsupportedSettingsDocumentsAreQuarantined(string contents)
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, contents);
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.NotNull(service.LastLoadNotice);
        Assert.NotNull(service.LastLoadNotice.QuarantinedPath);
        Assert.Equal(AppTheme.System, service.Current.Settings.Appearance.Theme);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "window": { "width": "wide" } }""")]
    [InlineData("""{ "schemaVersion": 1, "appearance": { "theme": "purple" } }""")]
    [InlineData("""{ "schemaVersion": 1, "logging": { "maxUiLines": [] } }""")]
    public async Task WrongTypedSettingsAreQuarantinedAndDefaultsAreUsed(string contents)
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, contents);
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.NotNull(service.LastLoadNotice);
        Assert.False(service.LastLoadNotice.RecoveredFromBackup);
        Assert.NotNull(service.LastLoadNotice.QuarantinedPath);
        Assert.True(File.Exists(service.LastLoadNotice.QuarantinedPath));
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.Equal(AppTheme.System, service.Current.Settings.Appearance.Theme);
        Assert.Equal(1280, service.Current.Settings.Window.Width);
    }

    [Fact]
    public async Task WrongTypedBackupIsQuarantinedWithDefaults()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, "corrupt");
        await File.WriteAllTextAsync(
            paths.SettingsBackupFile,
            """{ "schemaVersion": 1, "window": { "width": "wide" } }""");
        using var service = CreateService(temp);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.NotNull(service.LastLoadNotice);
        Assert.False(service.LastLoadNotice.RecoveredFromBackup);
        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(File.Exists(paths.SettingsBackupFile));
        Assert.Equal(2, Directory.EnumerateFiles(paths.QuarantineDirectory, "*.corrupt").Count());
        Assert.Equal(1280, service.Current.Settings.Window.Width);
    }

    [Fact]
    public async Task SetRecoversFromWrongTypedSettingsBeforeWriting()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "schemaVersion": 1, "window": { "width": "wide" } }""");
        using var service = CreateService(temp);

        await service.SetAsync(SettingsPaths.Density, "compact");

        Assert.True(service.IsInitialized);
        Assert.NotNull(service.LastLoadNotice);
        Assert.Equal("compact", service.GetString(SettingsPaths.Density));
        using var document = await ReadDocumentAsync(paths.SettingsFile);
        Assert.Equal("compact", document.RootElement.GetProperty("appearance").GetProperty("density").GetString());
    }

    [Fact]
    public async Task UnknownKeysArePreservedAcrossWrites()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "schemaVersion": 1, "experimental": { "newFlag": true } }""");
        using var service = CreateService(temp);
        await service.InitializeAsync();

        Assert.True(service.GetValue<bool>("experimental.newFlag"));

        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);

        using var document = await ReadDocumentAsync(paths.SettingsFile);
        Assert.True(document.RootElement.GetProperty("experimental").GetProperty("newFlag").GetBoolean());
    }

    [Fact]
    public async Task SetLoadsExistingSettingsBeforeWriting()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SettingsFile,
            """{ "schemaVersion": 1, "appearance": { "theme": "dark" } }""");
        using var service = CreateService(temp);

        await service.SetAsync(SettingsPaths.Density, "compact");

        Assert.Equal(AppTheme.Dark, service.GetValue<AppTheme>(SettingsPaths.Theme));
        Assert.Equal("compact", service.GetString(SettingsPaths.Density));
        Assert.True(service.IsInitialized);
    }

    [Fact]
    public async Task InvalidTypedReadsReturnDefaultWithoutThrowing()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        Assert.Equal(0, service.GetValue<int>(SettingsPaths.Theme));
        Assert.Null(service.GetValue<string>("does.not.exist"));
        Assert.Null(service.GetString("does.not.exist"));
    }

    [Fact]
    public async Task NonGlobalScopesAreRejected()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.SetAsync(SettingsPaths.Theme, AppTheme.Dark, ConfigScope.Profile));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.ResetAsync(SettingsPaths.Theme, ConfigScope.LocalProject));
    }

    [Theory]
    [InlineData("")]
    [InlineData("appearance..theme")]
    [InlineData(".appearance.theme")]
    [InlineData("appearance.theme.")]
    public async Task InvalidPathsAreRejected(string path)
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SetAsync(path, "value"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ResetAsync(path));
    }

    [Fact]
    public async Task ServiceRejectsUseAfterDispose()
    {
        using var temp = new TempDirectory();
        var service = CreateService(temp);
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.InitializeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.SetAsync(SettingsPaths.Theme, AppTheme.Dark));
        service.Dispose();
    }

    private static ConfigurationService CreateService(TempDirectory temp)
        => new(Paths(temp));

    private static AppPaths Paths(TempDirectory temp) => new(temp.Combine("data"));

    private static async Task<JsonDocument> ReadDocumentAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonDocument.Parse(json);
    }
}
