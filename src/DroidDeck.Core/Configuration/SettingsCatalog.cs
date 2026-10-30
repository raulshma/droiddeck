using System.Text.Json.Nodes;

namespace DroidDeck.Core.Configuration;

/// <summary>How much detail a settings group requires from the user.</summary>
public enum SettingsLevel
{
    /// <summary>Everyday settings shown by default.</summary>
    Basic,

    /// <summary>Settings that need some knowledge of the toolchain or machine.</summary>
    Advanced,

    /// <summary>Internal state and diagnostic settings.</summary>
    Expert,
}

/// <summary>The shape of a setting value.</summary>
public enum SettingsValueKind
{
    /// <summary>A free-form string.</summary>
    Text,

    /// <summary>A boolean toggle.</summary>
    Boolean,

    /// <summary>A whole number.</summary>
    WholeNumber,

    /// <summary>A decimal number.</summary>
    Number,

    /// <summary>One of a fixed set of strings.</summary>
    Enum,

    /// <summary>A file or directory path.</summary>
    Path,

    /// <summary>A list of strings.</summary>
    List,
}

/// <summary>A settings search request.</summary>
/// <param name="Text">Free text matched against names, descriptions, paths, keywords, and current values.</param>
/// <param name="Category">Restricts results to one category or section name.</param>
/// <param name="MaxLevel">Hides settings that require more detail than this level.</param>
public sealed record SettingsSearchQuery(
    string? Text = null,
    string? Category = null,
    SettingsLevel MaxLevel = SettingsLevel.Expert);

/// <summary>Describes one searchable setting.</summary>
public sealed record SettingsCatalogEntry
{
    /// <summary>Gets the dotted configuration path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the top-level section name.</summary>
    public required string Section { get; init; }

    /// <summary>Gets the user-facing category name.</summary>
    public required string Category { get; init; }

    /// <summary>Gets the user-facing setting name.</summary>
    public required string Label { get; init; }

    /// <summary>Gets the one-sentence explanation.</summary>
    public required string Description { get; init; }

    /// <summary>Gets the shape of the value.</summary>
    public required SettingsValueKind Kind { get; init; }

    /// <summary>Gets the detail level that reveals the setting.</summary>
    public SettingsLevel Level { get; init; } = SettingsLevel.Advanced;

    /// <summary>Gets the allowed values for an <see cref="SettingsValueKind.Enum"/> setting.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Gets additional search terms.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Gets whether changing the value requires a restart.</summary>
    public bool RestartRequired { get; init; }
}

/// <summary>
/// Describes every setting the host understands so the settings page can search it,
/// and so imports and the health panel can tell known paths from unknown ones.
/// </summary>
public sealed class SettingsCatalog
{
    private readonly SettingsCatalogEntry[] _entries;
    private readonly Dictionary<string, SettingsCatalogEntry> _byPath;
    private readonly string[] _categories;

    private SettingsCatalog(SettingsCatalogEntry[] entries)
    {
        _entries = entries;
        _byPath = new Dictionary<string, SettingsCatalogEntry>(entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            _byPath[entry.Path] = entry;
        }

        _categories = entries
            .Select(static entry => entry.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Gets the shared catalog.</summary>
    public static SettingsCatalog Default { get; } = new(CreateEntries());

    /// <summary>Gets every known setting.</summary>
    public IReadOnlyList<SettingsCatalogEntry> Entries => _entries;

    /// <summary>Gets the user-facing category names in declaration order.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <summary>Finds a setting by dotted path, ignoring case.</summary>
    public SettingsCatalogEntry? Find(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return _byPath.TryGetValue(path, out var entry) ? entry : null;
    }

    /// <summary>
    /// Searches by name, description, path, keywords, and, when a snapshot is supplied,
    /// the current value. Every whitespace-separated term must match. Results are ranked
    /// by where they matched: names first, then keywords and paths, then descriptions
    /// and current values.
    /// </summary>
    public IReadOnlyList<SettingsCatalogEntry> Search(SettingsSearchQuery query, ConfigSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = SplitTerms(query.Text);
        var category = query.Category?.Trim();
        var ranked = new List<(SettingsCatalogEntry Entry, int Score, int Index)>();

        for (var index = 0; index < _entries.Length; index++)
        {
            var entry = _entries[index];
            if (entry.Level > query.MaxLevel || !MatchesCategory(entry, category))
            {
                continue;
            }

            var score = Score(entry, terms, snapshot);
            if (score >= 0)
            {
                ranked.Add((entry, score, index));
            }
        }

        ranked.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Index.CompareTo(right.Index);
        });

        return ranked.Select(static item => item.Entry).ToArray();
    }

    private static int Score(SettingsCatalogEntry entry, string[] terms, ConfigSnapshot? snapshot)
    {
        if (terms.Length == 0)
        {
            return 1;
        }

        var currentValue = snapshot is null ? null : FormatValue(snapshot.GetValue(entry.Path));
        var total = 0;
        foreach (var term in terms)
        {
            var best = 0;
            if (entry.Label.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                best = 5;
            }
            else if (MatchesAny(entry.Keywords, term))
            {
                best = 4;
            }
            else if (entry.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                best = 3;
            }
            else if (entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                best = 2;
            }
            else if (currentValue is not null && currentValue.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                best = 1;
            }

            if (best == 0)
            {
                return -1;
            }

            total += best;
        }

        return total;
    }

    private static bool MatchesAny(IReadOnlyList<string> values, string term)
    {
        foreach (var value in values)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCategory(SettingsCatalogEntry entry, string? category) =>
        string.IsNullOrWhiteSpace(category) ||
        string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(entry.Section, category, StringComparison.OrdinalIgnoreCase);

    private static string[] SplitTerms(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FormatValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        JsonArray array => string.Join(", ", array.Select(FormatValue)),
        _ => node.ToJsonString(),
    };

    private static SettingsCatalogEntry[] CreateEntries()
    {
        var entries = new List<SettingsCatalogEntry>();

        Add(entries, "appearance", "theme", "Theme",
            "Follows the operating system or forces one appearance.",
            SettingsValueKind.Enum, SettingsLevel.Basic,
            options: ["system", "light", "dark"], keywords: ["appearance", "color scheme"]);
        Add(entries, "appearance", "themeId", "Theme file",
            "Selects a built-in or custom theme with color tokens, fonts, and log colors.",
            SettingsValueKind.Text, SettingsLevel.Advanced, keywords: ["appearance", "custom theme"]);
        Add(entries, "appearance", "accentColor", "Accent color",
            "Overrides the theme accent after contrast validation.",
            SettingsValueKind.Text, SettingsLevel.Advanced, keywords: ["appearance", "color"]);
        Add(entries, "appearance", "fontFamily", "UI font",
            "Overrides the theme font family.",
            SettingsValueKind.Text, SettingsLevel.Advanced, keywords: ["appearance", "typography"]);
        Add(entries, "appearance", "density", "Density",
            "Spacing between rows and cards across the app.",
            SettingsValueKind.Enum, SettingsLevel.Basic,
            options: ["comfortable", "compact"], keywords: ["layout", "spacing"]);
        Add(entries, "appearance", "motion", "Motion",
            "Controls animated transitions in the shell.",
            SettingsValueKind.Enum, SettingsLevel.Basic,
            options: ["full", "reduced"], keywords: ["animation", "accessibility"]);

        Add(entries, "tools", "adbPath", "adb path",
            "Overrides the automatically detected adb executable.");
        Add(entries, "tools", "androidSdkPath", "Android SDK path",
            "Overrides the Android SDK root used by build tools.");
        Add(entries, "tools", "aapt2Path", "aapt2 path",
            "Overrides the aapt2 executable used to read APK manifests.");
        Add(entries, "tools", "emulatorPath", "Emulator path",
            "Overrides the Android emulator executable.");
        Add(entries, "tools", "javaHome", "Java home",
            "Overrides the Java runtime directory.", keywords: ["jdk", "java"]);
        Add(entries, "tools", "gradlePath", "Gradle path",
            "Overrides the Gradle wrapper or executable.");
        Add(entries, "tools", "flutterPath", "Flutter path",
            "Overrides the Flutter SDK executable.");
        Add(entries, "tools", "dartPath", "Dart path",
            "Overrides the Dart executable.");
        Add(entries, "tools", "nodePath", "Node path",
            "Overrides the Node.js executable.");
        Add(entries, "tools", "scrcpyPath", "scrcpy path",
            "Overrides the scrcpy executable used for screen mirroring.");
        Add(entries, "tools", "unityPath", "Unity path",
            "Overrides the Unity editor executable.");
        Add(entries, "tools", "dotNetPath", "dotnet path",
            "Overrides the .NET SDK executable.", level: SettingsLevel.Expert);

        Add(entries, "logging", "level", "Log level",
            "Minimum severity written to the activity log.",
            SettingsValueKind.Enum, SettingsLevel.Basic,
            options: ["trace", "debug", "information", "warning", "error", "critical"],
            keywords: ["activity log", "verbosity"]);
        Add(entries, "logging", "maxUiLines", "Activity log line limit",
            "How many log lines the activity view keeps in memory.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["activity log", "buffer"]);
        Add(entries, "logging", "retentionDays", "Session retention (days)",
            "How long captured sessions are kept before cleanup.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["sessions", "cleanup"]);
        Add(entries, "logging", "maxSessionBytes", "Session storage limit (bytes)",
            "Total bytes of captured sessions kept on disk.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["sessions", "disk"]);

        Add(entries, "diagnostics", "inMemoryLogEntries", "In-memory log entries",
            "How many log entries diagnostics retain in memory.",
            SettingsValueKind.WholeNumber, SettingsLevel.Expert);
        Add(entries, "diagnostics", "recentErrorCount", "Recent errors in reports",
            "How many recent errors a diagnostics report includes.",
            SettingsValueKind.WholeNumber, SettingsLevel.Expert);

        Add(entries, "features", "developerMode", "Developer mode",
            "Shows developer tools such as the UI inspector.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["debug"]);
        Add(entries, "features", "safeMode", "Safe mode",
            "Starts DroidDeck without third-party plugins.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced,
            restartRequired: true, keywords: ["plugins", "troubleshooting"]);
        Add(entries, "features", "hidden", "Hidden features",
            "Navigation, toolbox, home, and stack features hidden in Customize mode.",
            SettingsValueKind.List, SettingsLevel.Advanced, keywords: ["customize", "navigation"]);

        Add(entries, "window", "width", "Window width",
            "Restored window width in effective pixels.", SettingsValueKind.Number, SettingsLevel.Expert);
        Add(entries, "window", "height", "Window height",
            "Restored window height in effective pixels.", SettingsValueKind.Number, SettingsLevel.Expert);
        Add(entries, "window", "x", "Window X position",
            "Restored horizontal window position.", SettingsValueKind.Number, SettingsLevel.Expert);
        Add(entries, "window", "y", "Window Y position",
            "Restored vertical window position.", SettingsValueKind.Number, SettingsLevel.Expert);
        Add(entries, "window", "isMaximized", "Window maximized",
            "Whether the main window was maximized.", SettingsValueKind.Boolean, SettingsLevel.Expert);

        Add(entries, "projects", "scanDepth", "Project scan depth",
            "How many folder levels a monorepo scan visits.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["discovery", "folders"]);
        Add(entries, "projects", "maxScanFolders", "Project scan folder limit",
            "Maximum number of folders a project scan visits.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["discovery"]);
        Add(entries, "projects", "scanTimeoutSeconds", "Project scan timeout (seconds)",
            "Maximum time a project scan may run.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["discovery"]);
        Add(entries, "projects", "ignoreFolders", "Ignored project folders",
            "Folder names that project discovery never scans.",
            SettingsValueKind.List, SettingsLevel.Advanced, keywords: ["discovery", "exclude"]);
        Add(entries, "projects", "scanMonorepos", "Scan monorepos",
            "Whether repository scans offer nested projects.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["discovery"]);

        Add(entries, "notifications", "enabled", "Notifications",
            "Master switch for in-app notifications.", SettingsValueKind.Boolean, SettingsLevel.Basic);
        Add(entries, "notifications", "runSucceeded", "Successful run notifications",
            "Notify when a run succeeds.", SettingsValueKind.Boolean, SettingsLevel.Basic);
        Add(entries, "notifications", "runFailed", "Failed run notifications",
            "Notify when a run fails.", SettingsValueKind.Boolean, SettingsLevel.Basic);
        Add(entries, "notifications", "runCancelled", "Cancelled run notifications",
            "Notify when a run is cancelled.", SettingsValueKind.Boolean, SettingsLevel.Advanced);
        Add(entries, "notifications", "crash", "Crash notifications",
            "Notify when an app crash is captured.", SettingsValueKind.Boolean, SettingsLevel.Basic);
        Add(entries, "notifications", "installFailed", "Install failure notifications",
            "Notify when an install fails.", SettingsValueKind.Boolean, SettingsLevel.Advanced);

        Add(entries, "home", "lastProjectId", "Last quick-run project",
            "Project restored by the home cockpit.", SettingsValueKind.Text, SettingsLevel.Expert);
        Add(entries, "home", "lastConfigurationId", "Last quick-run configuration",
            "Run configuration restored by the home cockpit.", SettingsValueKind.Text, SettingsLevel.Expert);
        Add(entries, "home", "lastDeviceSerial", "Last quick-run device",
            "Device restored by the home cockpit.", SettingsValueKind.Text, SettingsLevel.Expert);

        Add(entries, "onboarding", "completed", "Onboarding completed",
            "Whether the first-run flow finished.", SettingsValueKind.Boolean, SettingsLevel.Expert);
        Add(entries, "onboarding", "step", "Onboarding step",
            "The last first-run step reached.", SettingsValueKind.Text, SettingsLevel.Expert);

        Add(entries, "updates", "manualCheckEnabled", "Manual update checks",
            "Allows checking for a newer release from the About page. DroidDeck never checks automatically.",
            SettingsValueKind.Boolean, SettingsLevel.Basic, keywords: ["updates", "version", "release"]);

        Add(entries, "run", "maxConcurrentDevices", "Concurrent devices",
            "How many devices a multi-device run mutates at once.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["parallel", "multi-device"]);
        Add(entries, "run", "buildOnce", "Build once for multi-device runs",
            "Builds one artifact and reuses it for every device.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["multi-device"]);

        Add(entries, "screen", "hostingMode", "Screen hosting mode",
            "Where mirrored devices are hosted: auto, docked, external, or popout.",
            SettingsValueKind.Enum, SettingsLevel.Advanced,
            options: ["auto", "docked", "external", "popout"], keywords: ["scrcpy", "mirroring"]);
        Add(entries, "screen", "maxMirrors", "Maximum concurrent mirrors",
            "The maximum number of mirrored devices running at once.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["scrcpy", "mirroring"]);
        Add(entries, "screen", "maxRecordings", "Maximum concurrent recordings",
            "The maximum number of screen recordings running at once.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["scrcpy", "recording"]);
        Add(entries, "screen", "maxConcurrentBuilds", "Maximum concurrent builds",
            "The maximum number of builds counted by the resource guard.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["resources"]);
        Add(entries, "screen", "warnAtPercent", "Resource warning threshold",
            "Percent of a resource budget at which a warning appears.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["resources"]);
        Add(entries, "screen", "maxSize", "Maximum size",
            "Caps the mirrored video width at this many pixels.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["scrcpy", "resolution", "width"]);
        Add(entries, "screen", "maxFps", "Maximum frame rate",
            "Caps the mirrored video frame rate.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["scrcpy", "fps"]);
        Add(entries, "screen", "videoBitRate", "Video bit rate",
            "Sets the mirrored video bit rate, such as 8M.",
            SettingsValueKind.Text, SettingsLevel.Advanced, keywords: ["scrcpy"]);
        Add(entries, "screen", "stayAwake", "Stay awake while mirroring",
            "Keeps the device awake during mirroring.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["scrcpy"]);
        Add(entries, "screen", "clipboardSync", "Clipboard sync",
            "Shares the clipboard between the host and the device.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["scrcpy"]);
        Add(entries, "screen", "audio", "Mirror audio",
            "Captures device audio while mirroring.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["scrcpy", "sound"]);
        Add(entries, "screen", "screenshotsDirectory", "Screenshots directory",
            "Where screenshots are saved, or empty for the default.",
            SettingsValueKind.Path, SettingsLevel.Advanced, keywords: ["screenshot", "folder"]);
        Add(entries, "screen", "recordingsDirectory", "Recordings directory",
            "Where recordings are saved, or empty for the default.",
            SettingsValueKind.Path, SettingsLevel.Advanced, keywords: ["recording", "folder"]);
        Add(entries, "screen", "thumbnailsEnabled", "Device thumbnails",
            "Captures small device previews for the device list.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced);
        Add(entries, "screen", "thumbnailFps", "Thumbnail capture rate",
            "Thumbnail captures per second for the device list.",
            SettingsValueKind.Number, SettingsLevel.Advanced);

        Add(entries, "wireless", "autoReconnect", "Wireless auto reconnect",
            "Reconnects remembered wireless devices automatically.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["adb", "wifi"]);
        Add(entries, "wireless", "reconnectDelaySeconds", "Wireless reconnect delay (seconds)",
            "How long to wait between wireless reconnect attempts.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["adb", "wifi"]);

        Add(entries, "performance", "sampleIntervalMs", "Performance sample interval (ms)",
            "How often CPU, memory, and frame samples are captured.",
            SettingsValueKind.WholeNumber, SettingsLevel.Basic, keywords: ["sampling", "polling"]);
        Add(entries, "performance", "maxSamples", "Performance sample retention",
            "How many performance samples charts keep.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["charts", "history"]);

        Add(entries, "inspector", "maxDepth", "Inspector depth limit",
            "Maximum node depth shown in the UI hierarchy tree.",
            SettingsValueKind.WholeNumber, SettingsLevel.Advanced, keywords: ["ui", "hierarchy"]);
        Add(entries, "inspector", "includeInvisible", "Show invisible nodes",
            "Includes invisible nodes in the UI hierarchy tree.",
            SettingsValueKind.Boolean, SettingsLevel.Advanced, keywords: ["ui", "hierarchy"]);

        Add(entries, "profile", "activeId", "Active profile",
            "The profile whose settings layer is applied.",
            SettingsValueKind.Text, SettingsLevel.Advanced, keywords: ["profile"]);

        return [.. entries];
    }

    private static void Add(
        List<SettingsCatalogEntry> entries,
        string section,
        string key,
        string label,
        string description,
        SettingsValueKind kind = SettingsValueKind.Path,
        SettingsLevel level = SettingsLevel.Advanced,
        string[]? options = null,
        string[]? keywords = null,
        bool restartRequired = false) =>
        entries.Add(new SettingsCatalogEntry
        {
            Path = section + "." + key,
            Section = section,
            Category = CategoryFor(section),
            Label = label,
            Description = description,
            Kind = kind,
            Level = level,
            Options = options ?? [],
            Keywords = keywords ?? [],
            RestartRequired = restartRequired,
        });

    private static string CategoryFor(string section) => section switch
    {
        "appearance" => "Appearance",
        "tools" => "Tools",
        "logging" => "Logging",
        "diagnostics" => "Diagnostics",
        "features" => "Features",
        "window" => "Window",
        "projects" => "Projects",
        "notifications" => "Notifications",
        "home" => "Home",
        "onboarding" => "Onboarding",
        "updates" => "Updates",
        "run" => "Runs",
        "screen" => "Screen",
        "wireless" => "Wireless",
        "performance" => "Performance",
        "inspector" => "Inspector",
        "profile" => "Profiles",
        _ => char.ToUpperInvariant(section[0]) + section[1..],
    };
}
