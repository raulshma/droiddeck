using DroidDeck.Core.Configuration;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Toolchain;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Tests.Diagnostics;

public sealed class DiagnosticsServiceTests
{
    [Fact]
    public async Task BuildReport_IncludesVersionsSourcesDoctorDevicesAndErrors()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var configuration = new ConfigurationService(paths);
        await configuration.InitializeAsync();
        await configuration.SetAsync(SettingsPaths.Theme, AppTheme.Dark);

        var log = new BoundedLogBuffer();
        log.Add(new LogEntry(
            DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            LogLevel.Error,
            "DroidDeck.Adb",
            "adb failed with token=super-secret-token",
            "at Startup() password=hunter2",
            "ABCDEF123456"));

        var devices = new StubDeviceService
        {
            IsAdbConnected = true,
            Devices =
            [
                new DeviceInfo
                {
                    Serial = "emulator-5554",
                    State = DeviceState.Device,
                    Model = "Pixel 8",
                    AndroidVersion = "14",
                    ApiLevel = 34,
                    IsEmulator = true,
                },
            ],
        };
        var toolchain = new StubToolchainService
        {
            CachedSnapshot = new ToolchainSnapshot(
                [
                    new ToolInfo
                    {
                        Kind = ToolKind.Adb,
                        DisplayName = "adb",
                        IsAvailable = true,
                        Version = "35.0.1",
                        Path = "C:\\Android\\adb.exe",
                        Source = ToolDetectionSource.Automatic,
                    },
                ],
                DateTimeOffset.Parse("2026-10-28T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture)),
        };
        var session = new SessionContext("ABCDEF123456");
        var service = new DiagnosticsService(paths, configuration, toolchain, devices, log, session);

        var report = service.BuildReport();

        Assert.Contains("Diagnostics ID: DD-ABCDEF12", report, StringComparison.Ordinal);
        Assert.Contains("Session ID: ABCDEF123456", report, StringComparison.Ordinal);
        Assert.Contains("DroidDeck:", report, StringComparison.Ordinal);
        Assert.Contains("adb: 35.0.1", report, StringComparison.Ordinal);
        Assert.Contains("appearance.theme = \"dark\" [Global]", report, StringComparison.Ordinal);
        Assert.Contains("emulator-5554", report, StringComparison.Ordinal);
        Assert.Contains("Pixel 8", report, StringComparison.Ordinal);
        Assert.Contains("Recent errors (DD-ABCDEF12)", report, StringComparison.Ordinal);
        Assert.Contains("DroidDeck.Adb", report, StringComparison.Ordinal);
        Assert.Contains("ABCDEF123456", report, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-token", report, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildReport_CanOmitPathsAndLogs()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var configuration = new ConfigurationService(paths);
        await configuration.InitializeAsync();
        var service = new DiagnosticsService(
            paths,
            configuration,
            new StubToolchainService(),
            new StubDeviceService(),
            new BoundedLogBuffer(),
            new SessionContext());

        var report = service.BuildReport(new DiagnosticsReportOptions
        {
            IncludePaths = false,
            IncludeRecentLogs = false,
        });

        Assert.DoesNotContain("## Paths", report, StringComparison.Ordinal);
        Assert.DoesNotContain("## Recent log entries", report, StringComparison.Ordinal);
        Assert.Contains("## Recent errors", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveReportAsync_WritesRedactedTextUnderDiagnosticsFolder()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var configuration = new ConfigurationService(paths);
        await configuration.InitializeAsync();
        var log = new BoundedLogBuffer();
        log.Add(new LogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Error,
            "Tests",
            "secret=do-not-write",
            SessionId: "SESSION"));
        var service = new DiagnosticsService(
            paths,
            configuration,
            new StubToolchainService(),
            new StubDeviceService(),
            log,
            new SessionContext("SESSION"));

        var path = await service.SaveReportAsync();

        Assert.StartsWith(paths.DiagnosticsDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("Diagnostics ID: DD-SESSION", text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-write", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plugin settings never enter reports structurally: the plugin health
    /// section carries statuses and reasons only, and schema-driven plugin
    /// settings never hold secret values on disk in the first place. This test
    /// pins the second line of defense: a plugin-settings-shaped section is
    /// redacted like any other content.
    /// </summary>
    [Fact]
    public void BuildReport_RedactsPluginSettingsShapedExtraSections()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var configuration = new ConfigurationService(paths);
        var service = new DiagnosticsService(
            paths,
            configuration,
            new StubToolchainService(),
            new StubDeviceService(),
            new BoundedLogBuffer(),
            new SessionContext());
        const string leakedDump = """
            Plugin settings for com.example.sample:
            - apiKey = plugin-secret-value-555
            - password: "hunter2-with-entropy"
            - endpoint = https://example.com
            """;

        var report = service.BuildReport(new DiagnosticsReportOptions
        {
            ExtraSections = [new DiagnosticsSection("Plugin settings", leakedDump)],
        });

        Assert.DoesNotContain("plugin-secret-value-555", report, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-with-entropy", report, StringComparison.Ordinal);
        Assert.Contains("<redacted>", report, StringComparison.Ordinal);
        Assert.Contains("https://example.com", report, StringComparison.Ordinal);
    }

    private sealed class StubToolchainService : IToolchainService
    {
        public ToolchainSnapshot? CachedSnapshot { get; set; }

        public Task<ToolchainSnapshot?> LoadCachedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedSnapshot);

        public Task<ToolchainSnapshot> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedSnapshot ?? new ToolchainSnapshot());

        public event EventHandler<ToolchainSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class StubDeviceService : IDeviceService
    {
        public IReadOnlyList<DeviceInfo> Devices { get; set; } = [];

        public bool IsAdbConnected { get; set; }

        public bool IsUsingCliFallback { get; set; }

        public string? LastError { get; set; }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DeviceMetadata> RefreshMetadataAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceMetadata());

        public Task<DeviceInfo?> GetDeviceAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult(Devices.FirstOrDefault(device => device.Serial == serial));

        public Task SetNotesAsync(string serial, string? notes, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetTagsAsync(string serial, IReadOnlyList<string> tags, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> RestartAdbAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public event EventHandler<DeviceListChangedEventArgs>? DevicesChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<DeviceInfo>? DeviceUpdated
        {
            add { }
            remove { }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
