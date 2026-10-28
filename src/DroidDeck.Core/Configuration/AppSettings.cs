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

    /// <summary>Gets or sets the Node executable path.</summary>
    public string? NodePath { get; set; }

    /// <summary>Gets or sets the scrcpy executable path.</summary>
    public string? ScrcpyPath { get; set; }

    /// <summary>Gets or sets the Unity editor executable path.</summary>
    public string? UnityPath { get; set; }

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

    /// <summary>Node path override.</summary>
    public const string NodePath = "tools.nodePath";

    /// <summary>scrcpy path override.</summary>
    public const string ScrcpyPath = "tools.scrcpyPath";

    /// <summary>Unity path override.</summary>
    public const string UnityPath = "tools.unityPath";

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
}
