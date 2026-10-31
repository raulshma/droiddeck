using System.Text.Json.Nodes;
using DroidDeck.Core.Toolchain;

namespace DroidDeck.Core.Configuration;

/// <summary>How serious a settings health finding is.</summary>
public enum SettingsHealthSeverity
{
    /// <summary>Informational only.</summary>
    Info,

    /// <summary>The configuration works but is likely to surprise the user.</summary>
    Warning,

    /// <summary>The configuration cannot be applied cleanly.</summary>
    Error,
}

/// <summary>The action that repairs a health finding.</summary>
public enum SettingsFixKind
{
    /// <summary>No automated fix; the user reviews the finding.</summary>
    None,

    /// <summary>Removes the override so the lower layer or default applies.</summary>
    ResetSetting,

    /// <summary>Removes a key this version does not understand.</summary>
    RemoveUnknownSetting,

    /// <summary>Replaces an out-of-range value with a supported one.</summary>
    ClampSetting,

    /// <summary>Runs the toolchain check again.</summary>
    RecheckTools,

    /// <summary>Removes lower-precedence overrides so the highest-precedence value wins alone.</summary>
    ResetOverrides,

    /// <summary>Opens the data folder, handled by the shell.</summary>
    OpenDataDirectory,
}

/// <summary>A repair offered for a health finding.</summary>
/// <param name="Kind">The repair action.</param>
/// <param name="Label">The button caption.</param>
public sealed record SettingsFix(SettingsFixKind Kind, string Label)
{
    /// <summary>Gets a finding without an automated repair.</summary>
    public static SettingsFix None { get; } = new(SettingsFixKind.None, string.Empty);
}

/// <summary>One configuration health finding.</summary>
public sealed record SettingsHealthIssue
{
    /// <summary>Gets the stable finding identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the severity.</summary>
    public required SettingsHealthSeverity Severity { get; init; }

    /// <summary>Gets the short title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the explanation.</summary>
    public required string Detail { get; init; }

    /// <summary>Gets the affected setting path, when one exists.</summary>
    public string? Path { get; init; }

    /// <summary>Gets the layer the fix applies to.</summary>
    public ConfigScope Scope { get; init; } = ConfigScope.Global;

    /// <summary>Gets the offered repair.</summary>
    public SettingsFix Fix { get; init; } = SettingsFix.None;

    /// <summary>Gets the replacement value for a clamp fix.</summary>
    public JsonNode? FixValue { get; init; }
}

/// <summary>The result of checking the configuration for problems.</summary>
/// <param name="Issues">Every finding.</param>
public sealed record SettingsHealthReport(IReadOnlyList<SettingsHealthIssue> Issues)
{
    /// <summary>Gets whether no warning or error was reported.</summary>
    public bool IsHealthy => !Issues.Any(static issue => issue.Severity != SettingsHealthSeverity.Info);
}

/// <summary>
/// Checks the effective configuration for recovery notices, overrides that shadow each
/// other, unknown keys, stale or missing tool paths, and out-of-range values, and applies
/// the offered repairs.
/// </summary>
public sealed class ConfigurationHealthService
{
    private static readonly ConfigScope[] ConflictScopes =
    [
        ConfigScope.Global,
        ConfigScope.Profile,
        ConfigScope.TeamProject,
        ConfigScope.LocalProject,
        ConfigScope.RunConfiguration,
        ConfigScope.Session,
    ];

    private static readonly (string Path, ToolKind Kind, bool IsDirectory)[] ToolSettings =
    [
        (SettingsPaths.AdbPath, ToolKind.Adb, false),
        (SettingsPaths.AndroidSdkPath, ToolKind.AndroidSdk, true),
        (SettingsPaths.Aapt2Path, ToolKind.Aapt2, false),
        (SettingsPaths.EmulatorPath, ToolKind.Emulator, false),
        (SettingsPaths.JavaHome, ToolKind.Java, true),
        (SettingsPaths.GradlePath, ToolKind.Gradle, false),
        (SettingsPaths.FlutterPath, ToolKind.Flutter, false),
        (SettingsPaths.DartPath, ToolKind.Dart, false),
        (SettingsPaths.NodePath, ToolKind.Node, false),
        (SettingsPaths.ScrcpyPath, ToolKind.Scrcpy, false),
        (SettingsPaths.UnityPath, ToolKind.Unity, false),
        (SettingsPaths.DotNetPath, ToolKind.DotNet, false),
    ];

    private readonly IConfigurationService _configuration;
    private readonly IToolchainService? _toolchain;

    /// <summary>Creates a health service for the configuration.</summary>
    /// <param name="configuration">The configuration to inspect and repair.</param>
    /// <param name="toolchain">The toolchain used to report missing tools, when available.</param>
    public ConfigurationHealthService(IConfigurationService configuration, IToolchainService? toolchain = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _toolchain = toolchain;
    }

    /// <summary>Collects every health finding.</summary>
    public async Task<SettingsHealthReport> AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        await _configuration.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var issues = new List<SettingsHealthIssue>();
        var snapshot = _configuration.Current;

        AddLoadNotice(issues);
        AddUnknownKeys(issues, snapshot);
        AddConflicts(issues);
        AddToolIssues(issues, snapshot);
        AddRangeIssues(issues, snapshot);

        return new SettingsHealthReport(issues);
    }

    /// <summary>
    /// Applies the repair offered by a finding. Returns <see langword="false"/> when the
    /// fix needs the shell (opening the data folder) or cannot be applied.
    /// </summary>
    public async Task<bool> ApplyFixAsync(
        SettingsHealthIssue issue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issue);
        switch (issue.Fix.Kind)
        {
            case SettingsFixKind.ResetSetting:
            case SettingsFixKind.RemoveUnknownSetting:
                if (string.IsNullOrWhiteSpace(issue.Path) || issue.Scope == ConfigScope.Device)
                {
                    return false;
                }

                await _configuration.ResetAsync(issue.Path, issue.Scope, cancellationToken).ConfigureAwait(false);
                return true;
            case SettingsFixKind.ClampSetting:
                if (string.IsNullOrWhiteSpace(issue.Path) || issue.FixValue is null)
                {
                    return false;
                }

                if (issue.Scope == ConfigScope.Device)
                {
                    return false;
                }

                if (issue.Scope == ConfigScope.Profile)
                {
                    await _configuration.ResetAsync(issue.Path, issue.Scope, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                await _configuration
                    .SetAsync(issue.Path, issue.FixValue.GetValue<int>(), issue.Scope, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            case SettingsFixKind.RecheckTools:
                if (_toolchain is null)
                {
                    return false;
                }

                await _toolchain.CheckAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case SettingsFixKind.ResetOverrides:
                if (string.IsNullOrWhiteSpace(issue.Path))
                {
                    return false;
                }

                var overridden = ConflictScopes
                    .Where(scope => _configuration.GetValueAt(scope, issue.Path) is not null)
                    .ToArray();
                if (overridden.Length <= 1)
                {
                    return true;
                }

                foreach (var scope in overridden[..^1])
                {
                    await _configuration.ResetAsync(issue.Path, scope, cancellationToken).ConfigureAwait(false);
                }

                return true;
            default:
                return false;
        }
    }

    private void AddLoadNotice(List<SettingsHealthIssue> issues)
    {
        if (_configuration.LastLoadNotice is not { } notice)
        {
            return;
        }

        issues.Add(new SettingsHealthIssue
        {
            Id = "settings.load-recovery",
            Severity = SettingsHealthSeverity.Warning,
            Title = "Settings were recovered",
            Detail = notice.Message,
            Fix = new SettingsFix(SettingsFixKind.OpenDataDirectory, "Open data folder"),
        });
    }

    private static void AddUnknownKeys(List<SettingsHealthIssue> issues, ConfigSnapshot snapshot)
    {
        foreach (var pair in snapshot.GetSourceMap())
        {
            if (pair.Value.Scope != ConfigScope.Global ||
                pair.Key.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase) ||
                SettingsCatalog.Default.Find(pair.Key) is not null)
            {
                continue;
            }

            issues.Add(new SettingsHealthIssue
            {
                Id = "settings.unknown:" + pair.Key,
                Severity = SettingsHealthSeverity.Warning,
                Title = "Unknown setting",
                Detail = $"\"{pair.Key}\" is stored in the global settings but is not a setting this version understands.",
                Path = pair.Key,
                Fix = new SettingsFix(SettingsFixKind.RemoveUnknownSetting, "Remove setting"),
            });
        }
    }

    private void AddConflicts(List<SettingsHealthIssue> issues)
    {
        foreach (var entry in SettingsCatalog.Default.Entries)
        {
            var values = new List<(ConfigScope Scope, string Json)>();
            foreach (var scope in ConflictScopes)
            {
                var node = _configuration.GetValueAt(scope, entry.Path);
                if (node is not null)
                {
                    values.Add((scope, node.ToJsonString()));
                }
            }

            if (values.Select(static value => value.Json).Distinct(StringComparer.Ordinal).Count() <= 1)
            {
                continue;
            }

            issues.Add(new SettingsHealthIssue
            {
                Id = "settings.conflict:" + entry.Path,
                Severity = SettingsHealthSeverity.Warning,
                Title = "Setting is overridden in several scopes",
                Detail =
                    $"{entry.Path} is set differently in {string.Join(", ", values.Select(static value => DisplayScope(value.Scope)))}. " +
                    $"The highest-precedence scope wins; reset the other overrides to keep the {DisplayScope(values[^1].Scope)} value.",
                Path = entry.Path,
                Fix = new SettingsFix(SettingsFixKind.ResetOverrides, "Reset overrides"),
            });
        }
    }

    private void AddToolIssues(List<SettingsHealthIssue> issues, ConfigSnapshot snapshot)
    {
        foreach (var (path, kind, isDirectory) in ToolSettings)
        {
            var label = SettingsCatalog.Default.Find(path)?.Label ?? path;
            var configured = _configuration.GetString(path);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var exists = isDirectory ? Directory.Exists(configured) : File.Exists(configured);
                if (!exists)
                {
                    issues.Add(new SettingsHealthIssue
                    {
                        Id = "settings.tool-path:" + path,
                        Severity = SettingsHealthSeverity.Warning,
                        Title = label + " does not exist",
                        Detail =
                            $"The configured path \"{configured}\" does not exist. " +
                            "Clear the override to let DroidDeck detect the tool again.",
                        Path = path,
                        Scope = snapshot.GetSource(path).Scope,
                        Fix = new SettingsFix(SettingsFixKind.ResetSetting, "Clear override"),
                    });
                }

                continue;
            }

            if (_toolchain?.CachedSnapshot is not { } tools || tools.Get(kind) is not { IsAvailable: false } tool)
            {
                continue;
            }

            issues.Add(new SettingsHealthIssue
            {
                Id = "settings.tool-missing:" + path,
                Severity = SettingsHealthSeverity.Warning,
                Title = label + " was not found",
                Detail = string.IsNullOrWhiteSpace(tool.Guidance)
                    ? $"{tool.DisplayName} could not be detected. Run the toolchain check again after installing it."
                    : tool.Guidance,
                Path = path,
                Fix = new SettingsFix(SettingsFixKind.RecheckTools, "Recheck tools"),
            });
        }
    }

    private void AddRangeIssues(List<SettingsHealthIssue> issues, ConfigSnapshot snapshot)
    {
        foreach (var range in SettingsValuePolicy.Ranges)
        {
            var value = _configuration.GetValue<int>(range.Path);
            if (range.Contains(value))
            {
                continue;
            }

            var clamped = range.Clamp(value);
            var label = SettingsCatalog.Default.Find(range.Path)?.Label ?? range.Path;
            issues.Add(new SettingsHealthIssue
            {
                Id = "settings.range:" + range.Path,
                Severity = SettingsHealthSeverity.Warning,
                Title = label + " is out of range",
                Detail = $"{range.Path} is {value}; supported values are {range.Minimum} to {range.Maximum}.",
                Path = range.Path,
                Scope = snapshot.GetSource(range.Path).Scope,
                Fix = new SettingsFix(SettingsFixKind.ClampSetting, $"Use {clamped}"),
                FixValue = JsonValue.Create(clamped),
            });
        }
    }

    private static string DisplayScope(ConfigScope scope) => scope switch
    {
        ConfigScope.BuiltInDefaults => "Default",
        ConfigScope.Global => "Global",
        ConfigScope.Profile => "Profile",
        ConfigScope.TeamProject => "Team project",
        ConfigScope.LocalProject => "Project",
        ConfigScope.RunConfiguration => "Run configuration",
        ConfigScope.Device => "Device",
        ConfigScope.Session => "Session",
        _ => scope.ToString(),
    };
}
