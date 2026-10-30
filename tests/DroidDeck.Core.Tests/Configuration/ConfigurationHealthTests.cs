using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Toolchain;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class ConfigurationHealthTests
{
    [Fact]
    public async Task HealthyConfigurationReportsNoIssues()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        var health = new ConfigurationHealthService(service);

        var report = await health.AnalyzeAsync();

        Assert.True(report.IsHealthy);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task StaleToolPathIsReportedAndTheFixRemovesIt()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        var stale = temp.Combine("missing", "adb.exe");
        await service.SetAsync(SettingsPaths.AdbPath, stale);
        var health = new ConfigurationHealthService(service);

        var report = await health.AnalyzeAsync();

        var issue = Assert.Single(report.Issues, issue => issue.Path == SettingsPaths.AdbPath);
        Assert.Equal(SettingsHealthSeverity.Warning, issue.Severity);
        Assert.Equal(SettingsFixKind.ResetSetting, issue.Fix.Kind);
        Assert.Contains("does not exist", issue.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.True(await health.ApplyFixAsync(issue));
        Assert.Null(service.GetValueAt(ConfigScope.Global, SettingsPaths.AdbPath));
        Assert.DoesNotContain(
            (await health.AnalyzeAsync()).Issues,
            issue => issue.Path == SettingsPaths.AdbPath);
    }

    [Fact]
    public async Task MissingToolFromTheToolchainIsReportedWithARecheckFix()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        var snapshot = new ToolchainSnapshot(
        [
            new ToolInfo
            {
                Kind = ToolKind.Adb,
                DisplayName = "adb",
                IsAvailable = false,
                Source = ToolDetectionSource.Missing,
            },
        ],
            DateTimeOffset.UtcNow);
        var health = new ConfigurationHealthService(service, new FakeToolchain(snapshot));

        var report = await health.AnalyzeAsync();

        var issue = Assert.Single(report.Issues, issue => issue.Path == SettingsPaths.AdbPath);
        Assert.Equal(SettingsFixKind.RecheckTools, issue.Fix.Kind);
        Assert.Contains("adb", issue.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfRangeValuesAreReportedWithAClampedFix()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.PerformanceMaxSamples, 99_999);
        var health = new ConfigurationHealthService(service);

        var issue = Assert.Single(
            (await health.AnalyzeAsync()).Issues,
            issue => issue.Path == SettingsPaths.PerformanceMaxSamples);
        Assert.Equal(SettingsFixKind.ClampSetting, issue.Fix.Kind);
        Assert.Equal(PerformanceSettings.MaximumSampleCount, issue.FixValue!.GetValue<int>());

        Assert.True(await health.ApplyFixAsync(issue));
        Assert.Equal(PerformanceSettings.MaximumSampleCount, service.GetValue<int>(SettingsPaths.PerformanceMaxSamples));
        Assert.DoesNotContain(
            (await health.AnalyzeAsync()).Issues,
            issue => issue.Path == SettingsPaths.PerformanceMaxSamples);
    }

    [Fact]
    public async Task ConflictingScopeValuesAreReported()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Dark);
        await service.SetAsync(SettingsPaths.Theme, AppTheme.Light, ConfigScope.Session);
        var health = new ConfigurationHealthService(service);

        var issue = Assert.Single(
            (await health.AnalyzeAsync()).Issues,
            issue => issue.Path == SettingsPaths.Theme);

        Assert.Equal(SettingsHealthSeverity.Warning, issue.Severity);
        Assert.Equal(SettingsFixKind.ResetOverrides, issue.Fix.Kind);
        Assert.Contains("Global", issue.Detail, StringComparison.Ordinal);
        Assert.Contains("Session", issue.Detail, StringComparison.Ordinal);

        Assert.True(await health.ApplyFixAsync(issue));
        Assert.Null(service.GetValueAt(ConfigScope.Global, SettingsPaths.Theme));
        Assert.NotNull(service.GetValueAt(ConfigScope.Session, SettingsPaths.Theme));
        Assert.DoesNotContain(
            (await health.AnalyzeAsync()).Issues,
            finding => finding.Id == "settings.conflict:" + SettingsPaths.Theme);
    }

    [Fact]
    public async Task UnknownGlobalSettingsCanBeRemoved()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync("custom.typo", 1);
        var health = new ConfigurationHealthService(service);

        var issue = Assert.Single(
            (await health.AnalyzeAsync()).Issues,
            issue => issue.Path == "custom.typo");
        Assert.Equal(SettingsFixKind.RemoveUnknownSetting, issue.Fix.Kind);

        Assert.True(await health.ApplyFixAsync(issue));
        Assert.Null(service.GetValueAt(ConfigScope.Global, "custom.typo"));
    }

    [Fact]
    public async Task LoadRecoveryIsReportedWithTheDataFolderFix()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("data", "settings.json"), "{ this is not json");
        using var service = CreateService(temp);
        await service.InitializeAsync();
        var health = new ConfigurationHealthService(service);

        var report = await health.AnalyzeAsync();

        Assert.NotNull(service.LastLoadNotice);
        var issue = Assert.Single(report.Issues, issue => issue.Id == "settings.load-recovery");
        Assert.Equal(SettingsFixKind.OpenDataDirectory, issue.Fix.Kind);
        Assert.False(await health.ApplyFixAsync(issue));
    }

    private static ConfigurationService CreateService(TempDirectory temp) => new(new AppPaths(temp.Combine("data")));

    private sealed class FakeToolchain(ToolchainSnapshot snapshot) : IToolchainService
    {
        public ToolchainSnapshot? CachedSnapshot { get; } = snapshot;

        public Task<ToolchainSnapshot?> LoadCachedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ToolchainSnapshot?>(CachedSnapshot);

        public Task<ToolchainSnapshot> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedSnapshot!);

        public event EventHandler<ToolchainSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }
}
