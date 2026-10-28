using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Configuration;

/// <summary>The application theme preference.</summary>
public enum AppTheme
{
    /// <summary>Follow the operating system.</summary>
    System,

    /// <summary>Always use the light theme.</summary>
    Light,

    /// <summary>Always use the dark theme.</summary>
    Dark,
}

/// <summary>Root of the typed global settings document.</summary>
public sealed class AppSettings
{
    /// <summary>The current settings schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets or sets the schema version of the document.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Gets or sets appearance settings.</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>Gets or sets tool path overrides.</summary>
    public ToolsSettings Tools { get; set; } = new();

    /// <summary>Gets or sets logging settings.</summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>Gets or sets diagnostics settings.</summary>
    public DiagnosticsSettings Diagnostics { get; set; } = new();

    /// <summary>Gets or sets feature flags.</summary>
    public FeatureSettings Features { get; set; } = new();

    /// <summary>Gets or sets window placement.</summary>
    public WindowSettings Window { get; set; } = new();

    /// <summary>Gets or sets project discovery settings.</summary>
    public ProjectsSettings Projects { get; set; } = new();

    /// <summary>Gets or sets notification settings.</summary>
    public NotificationsSettings Notifications { get; set; } = new();

    /// <summary>Gets or sets home cockpit state.</summary>
    public HomeSettings Home { get; set; } = new();

    /// <summary>Gets or sets first-run onboarding state.</summary>
    public OnboardingSettings Onboarding { get; set; } = new();

    /// <summary>Gets or sets multi-device run settings.</summary>
    public RunSettings Run { get; set; } = new();

    /// <summary>Gets or sets screen mirroring settings.</summary>
    public ScreenSettings Screen { get; set; } = new();

    /// <summary>Gets or sets wireless ADB settings.</summary>
    public WirelessSettings Wireless { get; set; } = new();

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Settings for running a project on multiple devices.</summary>
public sealed class RunSettings
{
    /// <summary>Gets or sets the global limit for concurrent device mutations.</summary>
    public int MaxConcurrentDevices { get; set; } = 3;

    /// <summary>Gets or sets whether a multi-device run builds once and reuses the artifact.</summary>
    public bool BuildOnce { get; set; } = true;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Settings for scrcpy mirroring, screenshots, and recordings.</summary>
public sealed class ScreenSettings
{
    /// <summary>Gets or sets the default hosting mode: auto, docked, external, or popout.</summary>
    public string HostingMode { get; set; } = "auto";

    /// <summary>Gets or sets the maximum number of concurrent mirrors.</summary>
    public int MaxMirrors { get; set; } = 3;

    /// <summary>Gets or sets the maximum number of concurrent recordings.</summary>
    public int MaxRecordings { get; set; } = 2;

    /// <summary>Gets or sets the maximum number of concurrent builds counted by the resource guard.</summary>
    public int MaxConcurrentBuilds { get; set; } = 1;

    /// <summary>Gets or sets the warning threshold percentage for the resource guard.</summary>
    public int WarnAtPercent { get; set; } = 80;

    /// <summary>Gets or sets the scrcpy maximum size in pixels.</summary>
    public int MaxSize { get; set; } = 1024;

    /// <summary>Gets or sets the scrcpy frame rate limit.</summary>
    public int MaxFps { get; set; } = 60;

    /// <summary>Gets or sets the scrcpy video bit rate, such as <c>8M</c>.</summary>
    public string VideoBitRate { get; set; } = "8M";

    /// <summary>Gets or sets whether the device stays awake while mirroring.</summary>
    public bool StayAwake { get; set; }

    /// <summary>Gets or sets whether clipboard synchronization is enabled.</summary>
    public bool ClipboardSync { get; set; } = true;

    /// <summary>Gets or sets whether audio is captured. Off by default to avoid desktop echo.</summary>
    public bool Audio { get; set; }

    /// <summary>Gets or sets the directory for screenshots, or <see langword="null"/> for the default.</summary>
    public string? ScreenshotsDirectory { get; set; }

    /// <summary>Gets or sets the directory for recordings, or <see langword="null"/> for the default.</summary>
    public string? RecordingsDirectory { get; set; }

    /// <summary>Gets or sets whether device-list thumbnails are captured.</summary>
    public bool ThumbnailsEnabled { get; set; } = true;

    /// <summary>Gets or sets the thumbnail capture rate per second.</summary>
    public double ThumbnailFps { get; set; } = 1;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Settings for wireless ADB pairing and reconnect.</summary>
public sealed class WirelessSettings
{
    /// <summary>Gets or sets whether remembered devices reconnect automatically.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Gets or sets the reconnect delay in seconds.</summary>
    public int ReconnectDelaySeconds { get; set; } = 10;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>The quick run target the home cockpit restores on start.</summary>
public sealed class HomeSettings
{
    /// <summary>Gets or sets the project the last quick run used.</summary>
    public string? LastProjectId { get; set; }

    /// <summary>Gets or sets the run configuration the last quick run used.</summary>
    public string? LastConfigurationId { get; set; }

    /// <summary>Gets or sets the device serial the last quick run used.</summary>
    public string? LastDeviceSerial { get; set; }

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>First-run onboarding progress.</summary>
public sealed class OnboardingSettings
{
    /// <summary>Gets or sets whether the onboarding flow finished.</summary>
    public bool Completed { get; set; }

    /// <summary>Gets or sets the step the user last reached ("tools", "project", or "device").</summary>
    public string Step { get; set; } = "tools";

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Appearance settings.</summary>
public sealed class AppearanceSettings
{
    /// <summary>Gets or sets the theme.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Gets or sets the density preset.</summary>
    public string Density { get; set; } = "comfortable";

    /// <summary>Gets or sets the motion preference.</summary>
    public string Motion { get; set; } = "full";

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Explicit tool paths that override auto-detection.</summary>
public sealed class ToolsSettings
{
    /// <summary>Gets or sets the adb executable path.</summary>
    public string? AdbPath { get; set; }

    /// <summary>Gets or sets the Android SDK root.</summary>
    public string? AndroidSdkPath { get; set; }

    /// <summary>Gets or sets the aapt2 executable path.</summary>
    public string? Aapt2Path { get; set; }

    /// <summary>Gets or sets the emulator executable path.</summary>
    public string? EmulatorPath { get; set; }

    /// <summary>Gets or sets the Java home directory.</summary>
    public string? JavaHome { get; set; }

    /// <summary>Gets or sets the Gradle executable path.</summary>
    public string? GradlePath { get; set; }

    /// <summary>Gets or sets the Flutter executable path.</summary>
    public string? FlutterPath { get; set; }

    /// <summary>Gets or sets the Dart executable path.</summary>
    public string? DartPath { get; set; }

    /// <summary>Gets or sets the Node executable path.</summary>
    public string? NodePath { get; set; }

    /// <summary>Gets or sets the scrcpy executable path.</summary>
    public string? ScrcpyPath { get; set; }

    /// <summary>Gets or sets the Unity editor executable path.</summary>
    public string? UnityPath { get; set; }

    /// <summary>Gets or sets the .NET SDK executable path.</summary>
    public string? DotNetPath { get; set; }

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Logging settings.</summary>
public sealed class LoggingSettings
{
    /// <summary>Gets or sets the minimum log level.</summary>
    public LogLevel Level { get; set; } = LogLevel.Information;

    /// <summary>Gets or sets the maximum number of log lines held by the UI.</summary>
    public int MaxUiLines { get; set; } = 20_000;

    /// <summary>Gets or sets the session retention period in days.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Gets or sets the maximum number of bytes retained for sessions.</summary>
    public long MaxSessionBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Diagnostics settings.</summary>
public sealed class DiagnosticsSettings
{
    /// <summary>Gets or sets the number of log entries retained in memory.</summary>
    public int InMemoryLogEntries { get; set; } = 2_000;

    /// <summary>Gets or sets the number of recent errors included in reports.</summary>
    public int RecentErrorCount { get; set; } = 100;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Feature flags.</summary>
public sealed class FeatureSettings
{
    /// <summary>Gets or sets whether developer mode is enabled.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Gets or sets whether safe mode is enabled.</summary>
    public bool SafeMode { get; set; }

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Persisted main window placement.</summary>
public sealed class WindowSettings
{
    /// <summary>Gets or sets the window width in effective pixels.</summary>
    public double Width { get; set; } = 1280;

    /// <summary>Gets or sets the window height in effective pixels.</summary>
    public double Height { get; set; } = 800;

    /// <summary>Gets or sets the window x position.</summary>
    public double? X { get; set; }

    /// <summary>Gets or sets the window y position.</summary>
    public double? Y { get; set; }

    /// <summary>Gets or sets whether the window was maximized.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Project discovery settings.</summary>
public sealed class ProjectsSettings
{
    /// <summary>Gets or sets how many folder levels a monorepo scan visits.</summary>
    public int ScanDepth { get; set; } = 3;

    /// <summary>Gets or sets the maximum number of folders visited by a scan.</summary>
    public int MaxScanFolders { get; set; } = 2_000;

    /// <summary>Gets or sets the scan timeout in seconds.</summary>
    public int ScanTimeoutSeconds { get; set; } = 2;

    /// <summary>Gets or sets folder names that are never scanned.</summary>
    public List<string> IgnoreFolders { get; set; } =
    [
        ".git", ".gradle", ".idea", "build", "node_modules", "dist", "out", "Pods",
        ".dart_tool", ".vs", "bin", "obj", "packages",
    ];

    /// <summary>Gets or sets whether scanning a repository offers nested projects.</summary>
    public bool ScanMonorepos { get; set; } = true;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>In-app notification settings.</summary>
public sealed class NotificationsSettings
{
    /// <summary>Gets or sets whether notifications are enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets whether successful runs notify.</summary>
    public bool RunSucceeded { get; set; } = true;

    /// <summary>Gets or sets whether failed runs notify.</summary>
    public bool RunFailed { get; set; } = true;

    /// <summary>Gets or sets whether cancelled runs notify.</summary>
    public bool RunCancelled { get; set; }

    /// <summary>Gets or sets whether crashes notify.</summary>
    public bool Crash { get; set; } = true;

    /// <summary>Gets or sets whether install failures notify.</summary>
    public bool InstallFailed { get; set; } = true;

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    /// <summary>Converts these settings into the notification service preference matrix.</summary>
    public Notifications.NotificationPreferences ToPreferences() => new()
    {
        Enabled = Enabled,
        OnRunSucceeded = RunSucceeded,
        OnRunFailed = RunFailed,
        OnRunCancelled = RunCancelled,
        OnCrash = Crash,
        OnInstallFailed = InstallFailed,
    };
}

/// <summary>Well-known configuration paths.</summary>
public static class SettingsPaths
{
    /// <summary>Theme path.</summary>
    public const string Theme = "appearance.theme";

    /// <summary>Density path.</summary>
    public const string Density = "appearance.density";

    /// <summary>Motion path.</summary>
    public const string Motion = "appearance.motion";

    /// <summary>ADB path override.</summary>
    public const string AdbPath = "tools.adbPath";

    /// <summary>Android SDK path override.</summary>
    public const string AndroidSdkPath = "tools.androidSdkPath";

    /// <summary>Java home override.</summary>
    public const string JavaHome = "tools.javaHome";

    /// <summary>aapt2 path override.</summary>
    public const string Aapt2Path = "tools.aapt2Path";

    /// <summary>Emulator path override.</summary>
    public const string EmulatorPath = "tools.emulatorPath";

    /// <summary>Gradle path override.</summary>
    public const string GradlePath = "tools.gradlePath";

    /// <summary>Flutter path override.</summary>
    public const string FlutterPath = "tools.flutterPath";

    /// <summary>Dart path override.</summary>
    public const string DartPath = "tools.dartPath";

    /// <summary>Node path override.</summary>
    public const string NodePath = "tools.nodePath";

    /// <summary>scrcpy path override.</summary>
    public const string ScrcpyPath = "tools.scrcpyPath";

    /// <summary>Unity path override.</summary>
    public const string UnityPath = "tools.unityPath";

    /// <summary>.NET SDK path override.</summary>
    public const string DotNetPath = "tools.dotNetPath";

    /// <summary>Log level path.</summary>
    public const string LogLevel = "logging.level";

    /// <summary>Session retention days path.</summary>
    public const string SessionRetentionDays = "logging.retentionDays";

    /// <summary>Session storage limit path.</summary>
    public const string SessionMaxBytes = "logging.maxSessionBytes";

    /// <summary>Window width path.</summary>
    public const string WindowWidth = "window.width";

    /// <summary>Window height path.</summary>
    public const string WindowHeight = "window.height";

    /// <summary>Window x position path.</summary>
    public const string WindowX = "window.x";

    /// <summary>Window y position path.</summary>
    public const string WindowY = "window.y";

    /// <summary>Window maximized path.</summary>
    public const string WindowIsMaximized = "window.isMaximized";

    /// <summary>Developer mode path.</summary>
    public const string DeveloperMode = "features.developerMode";

    /// <summary>Safe mode path.</summary>
    public const string SafeMode = "features.safeMode";

    /// <summary>Monorepo scan depth path.</summary>
    public const string ProjectScanDepth = "projects.scanDepth";

    /// <summary>Monorepo scan folder limit path.</summary>
    public const string ProjectMaxScanFolders = "projects.maxScanFolders";

    /// <summary>Monorepo scan timeout path.</summary>
    public const string ProjectScanTimeoutSeconds = "projects.scanTimeoutSeconds";

    /// <summary>Whether monorepo scans are enabled path.</summary>
    public const string ProjectScanMonorepos = "projects.scanMonorepos";

    /// <summary>Notification master switch path.</summary>
    public const string NotificationsEnabled = "notifications.enabled";

    /// <summary>Successful run notifications path.</summary>
    public const string NotifyRunSucceeded = "notifications.runSucceeded";

    /// <summary>Failed run notifications path.</summary>
    public const string NotifyRunFailed = "notifications.runFailed";

    /// <summary>Cancelled run notifications path.</summary>
    public const string NotifyRunCancelled = "notifications.runCancelled";

    /// <summary>Crash notifications path.</summary>
    public const string NotifyCrash = "notifications.crash";

    /// <summary>Install failure notifications path.</summary>
    public const string NotifyInstallFailed = "notifications.installFailed";

    /// <summary>Quick run project path.</summary>
    public const string HomeLastProjectId = "home.lastProjectId";

    /// <summary>Quick run configuration path.</summary>
    public const string HomeLastConfigurationId = "home.lastConfigurationId";

    /// <summary>Quick run device path.</summary>
    public const string HomeLastDeviceSerial = "home.lastDeviceSerial";

    /// <summary>Onboarding completed flag path.</summary>
    public const string OnboardingCompleted = "onboarding.completed";

    /// <summary>Onboarding last reached step path.</summary>
    public const string OnboardingStep = "onboarding.step";

    /// <summary>Multi-device concurrency limit path.</summary>
    public const string RunMaxConcurrentDevices = "run.maxConcurrentDevices";

    /// <summary>Build-once multi-device path.</summary>
    public const string RunBuildOnce = "run.buildOnce";

    /// <summary>Default screen hosting mode path.</summary>
    public const string ScreenHostingMode = "screen.hostingMode";

    /// <summary>Maximum concurrent mirrors path.</summary>
    public const string ScreenMaxMirrors = "screen.maxMirrors";

    /// <summary>Maximum concurrent recordings path.</summary>
    public const string ScreenMaxRecordings = "screen.maxRecordings";

    /// <summary>Maximum concurrent builds counted by the resource guard path.</summary>
    public const string ScreenMaxConcurrentBuilds = "screen.maxConcurrentBuilds";

    /// <summary>Resource warning threshold path.</summary>
    public const string ScreenWarnAtPercent = "screen.warnAtPercent";

    /// <summary>scrcpy maximum size path.</summary>
    public const string ScreenMaxSize = "screen.maxSize";

    /// <summary>scrcpy frame rate path.</summary>
    public const string ScreenMaxFps = "screen.maxFps";

    /// <summary>scrcpy bit rate path.</summary>
    public const string ScreenVideoBitRate = "screen.videoBitRate";

    /// <summary>Stay awake path.</summary>
    public const string ScreenStayAwake = "screen.stayAwake";

    /// <summary>Clipboard sync path.</summary>
    public const string ScreenClipboardSync = "screen.clipboardSync";

    /// <summary>Screen audio path.</summary>
    public const string ScreenAudio = "screen.audio";

    /// <summary>Screenshots directory path.</summary>
    public const string ScreenScreenshotsDirectory = "screen.screenshotsDirectory";

    /// <summary>Recordings directory path.</summary>
    public const string ScreenRecordingsDirectory = "screen.recordingsDirectory";

    /// <summary>Thumbnails enabled path.</summary>
    public const string ScreenThumbnailsEnabled = "screen.thumbnailsEnabled";

    /// <summary>Thumbnail rate path.</summary>
    public const string ScreenThumbnailFps = "screen.thumbnailFps";

    /// <summary>Wireless auto reconnect path.</summary>
    public const string WirelessAutoReconnect = "wireless.autoReconnect";

    /// <summary>Wireless reconnect delay path.</summary>
    public const string WirelessReconnectDelaySeconds = "wireless.reconnectDelaySeconds";
}
