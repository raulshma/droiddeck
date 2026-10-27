namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// Capabilities a plugin can declare. Capabilities are disclosed to the user; they are not a sandbox.
/// </summary>
[Flags]
public enum PluginCapability
{
    /// <summary>No capabilities.</summary>
    None = 0,

    /// <summary>Contributes a project detector.</summary>
    ProjectDetector = 1 << 0,

    /// <summary>Contributes a stack adapter.</summary>
    StackAdapter = 1 << 1,

    /// <summary>Contributes a run step.</summary>
    RunStep = 1 << 2,

    /// <summary>Contributes a device action.</summary>
    DeviceAction = 1 << 3,

    /// <summary>Contributes a UI page.</summary>
    UiPage = 1 << 4,

    /// <summary>Contributes a UI panel.</summary>
    UiPanel = 1 << 5,

    /// <summary>Contributes a renderer.</summary>
    Renderer = 1 << 6,

    /// <summary>Contributes a theme.</summary>
    Theme = 1 << 7,

    /// <summary>Contributes a command.</summary>
    Command = 1 << 8,

    /// <summary>Contributes a log parser.</summary>
    LogParser = 1 << 9,

    /// <summary>Contributes a test runner.</summary>
    TestRunner = 1 << 10,

    /// <summary>Contributes a home widget.</summary>
    HomeWidget = 1 << 11,

    /// <summary>Contributes a project tab.</summary>
    ProjectTab = 1 << 12,

    /// <summary>Contributes a device tab.</summary>
    DeviceTab = 1 << 13,

    /// <summary>Contributes a session panel.</summary>
    SessionPanel = 1 << 14,

    /// <summary>Contributes a toolbar action.</summary>
    ToolbarAction = 1 << 15,

    /// <summary>Contributes a settings section.</summary>
    SettingsSection = 1 << 16,

    /// <summary>Contributes highlight rules.</summary>
    HighlightRuleProvider = 1 << 17,

    /// <summary>Executes shell commands.</summary>
    ShellCommands = 1 << 18,

    /// <summary>Reads and writes plugin-private data.</summary>
    PluginData = 1 << 19,
}

/// <summary>
/// The result of parsing declared capability strings.
/// </summary>
/// <param name="Known">The recognized capabilities.</param>
/// <param name="Unknown">Capability names that are not recognized by this host.</param>
public sealed record PluginCapabilityParseResult(
    PluginCapability Known,
    IReadOnlyList<string> Unknown)
{
    /// <summary>Gets a value indicating whether any capability names were unknown.</summary>
    public bool HasUnknown => Unknown.Count > 0;
}

/// <summary>
/// Maps capability enum values to and from their manifest names.
/// </summary>
public static class PluginCapabilities
{
    private static readonly Dictionary<string, PluginCapability> ByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["project-detector"] = PluginCapability.ProjectDetector,
            ["stack-adapter"] = PluginCapability.StackAdapter,
            ["run-step"] = PluginCapability.RunStep,
            ["device-action"] = PluginCapability.DeviceAction,
            ["ui-page"] = PluginCapability.UiPage,
            ["ui-panel"] = PluginCapability.UiPanel,
            ["renderer"] = PluginCapability.Renderer,
            ["theme"] = PluginCapability.Theme,
            ["command"] = PluginCapability.Command,
            ["log-parser"] = PluginCapability.LogParser,
            ["test-runner"] = PluginCapability.TestRunner,
            ["home-widget"] = PluginCapability.HomeWidget,
            ["project-tab"] = PluginCapability.ProjectTab,
            ["device-tab"] = PluginCapability.DeviceTab,
            ["session-panel"] = PluginCapability.SessionPanel,
            ["toolbar-action"] = PluginCapability.ToolbarAction,
            ["settings-section"] = PluginCapability.SettingsSection,
            ["highlight-rule-provider"] = PluginCapability.HighlightRuleProvider,
            ["shell-commands"] = PluginCapability.ShellCommands,
            ["plugin-data"] = PluginCapability.PluginData,
        };

    /// <summary>
    /// Parses manifest capability names, retaining unknown names instead of failing.
    /// </summary>
    public static PluginCapabilityParseResult Parse(IEnumerable<string>? capabilities)
    {
        var known = PluginCapability.None;
        var unknown = new List<string>();
        if (capabilities is null)
        {
            return new PluginCapabilityParseResult(known, unknown);
        }

        foreach (var capability in capabilities)
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                continue;
            }

            if (ByName.TryGetValue(capability.Trim(), out var value))
            {
                known |= value;
            }
            else
            {
                unknown.Add(capability);
            }
        }

        return new PluginCapabilityParseResult(known, unknown);
    }

    /// <summary>Returns the canonical manifest name for a single capability.</summary>
    public static string ToManifestName(PluginCapability capability)
    {
        if (capability == PluginCapability.None)
        {
            throw new ArgumentOutOfRangeException(nameof(capability), "None has no manifest name.");
        }

        if ((capability & (capability - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capability), "Only single capabilities have a manifest name.");
        }

        foreach (var pair in ByName)
        {
            if (pair.Value == capability)
            {
                return pair.Key;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unknown capability.");
    }

    /// <summary>Formats a capability set as comma-separated manifest names.</summary>
    public static string ToDisplayString(PluginCapability capabilities)
    {
        if (capabilities == PluginCapability.None)
        {
            return "None";
        }

        var names = new List<string>();
        foreach (var capability in Enum.GetValues<PluginCapability>())
        {
            if (capability != PluginCapability.None && capabilities.HasFlag(capability))
            {
                names.Add(ToManifestName(capability).Replace('-', ' '));
            }
        }

        return string.Join(", ", names);
    }
}
