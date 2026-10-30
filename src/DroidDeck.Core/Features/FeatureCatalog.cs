namespace DroidDeck.Core.Features;

/// <summary>Where a customizable feature appears.</summary>
public enum FeatureCategory
{
    /// <summary>A primary navigation destination.</summary>
    Navigation,

    /// <summary>A toolbox or session workspace tab.</summary>
    Toolbox,

    /// <summary>A home dashboard widget.</summary>
    HomeWidget,

    /// <summary>A project stack or run workflow.</summary>
    Stack,
}

/// <summary>How completely a feature supports the active project or device.</summary>
public enum FeatureCapability
{
    /// <summary>The feature is fully supported.</summary>
    Full,

    /// <summary>The feature is available through command-line tools only.</summary>
    Cli,

    /// <summary>Only prebuilt APK workflows are available.</summary>
    ApkOnly,

    /// <summary>The workflow is user-defined.</summary>
    Custom,

    /// <summary>Some steps need manual work.</summary>
    Limited,

    /// <summary>The feature is hidden by the user.</summary>
    Hidden,

    /// <summary>The feature is unavailable on this machine or project.</summary>
    Unavailable,
}

/// <summary>A short explanation of a feature's support level.</summary>
/// <param name="Capability">The support level.</param>
/// <param name="Title">A two- or three-word label.</param>
/// <param name="Description">A sentence explaining what to expect.</param>
/// <param name="Tone">The visual tone the host should use.</param>
public sealed record CapabilityBadge(
    FeatureCapability Capability,
    string Title,
    string Description,
    CapabilityBadgeTone Tone);

/// <summary>The visual tone for a capability badge.</summary>
public enum CapabilityBadgeTone
{
    /// <summary>Normal, fully supported state.</summary>
    Neutral,

    /// <summary>Informational but expected state.</summary>
    Informational,

    /// <summary>Some functionality needs attention.</summary>
    Warning,

    /// <summary>Hidden or unavailable state.</summary>
    Muted,
}

/// <summary>A feature the user can hide without deleting its configuration.</summary>
/// <param name="Id">The stable feature identifier, also stored in <c>features.hidden</c>.</param>
/// <param name="Name">The display name.</param>
/// <param name="Category">Where the feature appears.</param>
/// <param name="Description">What the feature does.</param>
/// <param name="DefaultCapability">The normal support level.</param>
public sealed record FeatureDefinition(
    string Id,
    string Name,
    FeatureCategory Category,
    string Description,
    FeatureCapability DefaultCapability = FeatureCapability.Full);

/// <summary>The built-in list of hideable navigation, toolbox, home, and stack features.</summary>
public static class FeatureCatalog
{
    /// <summary>Gets every built-in feature.</summary>
    public static IReadOnlyList<FeatureDefinition> Default { get; } =
    [
        new("nav.home", "Home", FeatureCategory.Navigation, "The home cockpit with quick run and recent activity."),
        new("nav.devices", "Devices", FeatureCategory.Navigation, "Connected devices and device detail tools."),
        new("nav.projects", "Projects", FeatureCategory.Navigation, "Project discovery, detection, and run configurations."),
        new("nav.sessions", "Sessions", FeatureCategory.Navigation, "Active and historical run sessions."),
        new("nav.performance", "Performance", FeatureCategory.Navigation, "Live CPU, memory, and frame charts."),
        new("nav.inspector", "Inspector", FeatureCategory.Navigation, "The UI hierarchy inspector."),
        new("nav.mirror", "Mirror", FeatureCategory.Navigation, "The embedded screen mirror."),
        new("nav.history", "History", FeatureCategory.Navigation, "Past runs, installs, and device commands."),
        new("nav.settings", "Settings", FeatureCategory.Navigation, "Application, project, and plugin settings."),
        new("toolbox.logcat", "Logcat", FeatureCategory.Toolbox, "The filtered log stream and saved filters."),
        new("toolbox.screen", "Screen", FeatureCategory.Toolbox, "Screenshots, recording, and mirror controls."),
        new("toolbox.shell", "Shell", FeatureCategory.Toolbox, "A device shell with command history."),
        new("toolbox.files", "Files", FeatureCategory.Toolbox, "Device file browsing and transfer."),
        new("toolbox.performance", "Performance", FeatureCategory.Toolbox, "The session performance panel."),
        new("home.quickrun", "Quick run", FeatureCategory.HomeWidget, "The one-click project and device launcher."),
        new("home.recent", "Recent activity", FeatureCategory.HomeWidget, "Recent runs, installs, and captures."),
        new("home.devices", "Devices", FeatureCategory.HomeWidget, "A compact connected-device list."),
        new("home.plugins", "Plugin widgets", FeatureCategory.HomeWidget, "Widgets contributed by plugins."),
        new("stack.gradle", "Android Gradle", FeatureCategory.Stack, "Gradle build, install, and launch."),
        new("stack.flutter", "Flutter", FeatureCategory.Stack, "Flutter build and hot reload.", FeatureCapability.Limited),
        new("stack.reactnative", "React Native", FeatureCategory.Stack, "Metro plus the Android host build.", FeatureCapability.Limited),
        new("stack.maui", ".NET MAUI", FeatureCategory.Stack, ".NET Android build and deploy.", FeatureCapability.Limited),
        new("stack.apk", "APK only", FeatureCategory.Stack, "Install a prebuilt APK without building.", FeatureCapability.ApkOnly),
        new("stack.custom", "Custom steps", FeatureCategory.Stack, "User-defined build and run steps.", FeatureCapability.Custom),
    ];

    /// <summary>Finds a feature by identifier, ignoring case.</summary>
    public static FeatureDefinition? Find(string featureId) =>
        Default.FirstOrDefault(feature =>
            string.Equals(feature.Id, featureId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Explains capability levels consistently across the application.</summary>
public static class CapabilityBadges
{
    /// <summary>Gets the badge for a capability.</summary>
    public static CapabilityBadge For(FeatureCapability capability) => capability switch
    {
        FeatureCapability.Full => new(
            capability,
            "Full support",
            "This workflow is fully supported by DroidDeck.",
            CapabilityBadgeTone.Neutral),
        FeatureCapability.Cli => new(
            capability,
            "CLI support",
            "DroidDeck drives this workflow through command-line tools; some IDE-only features are unavailable.",
            CapabilityBadgeTone.Informational),
        FeatureCapability.ApkOnly => new(
            capability,
            "APK only",
            "DroidDeck installs and launches the APK, but does not build the project.",
            CapabilityBadgeTone.Informational),
        FeatureCapability.Custom => new(
            capability,
            "Custom workflow",
            "This project uses user-defined steps, so support depends on those steps.",
            CapabilityBadgeTone.Informational),
        FeatureCapability.Limited => new(
            capability,
            "Limited support",
            "Core actions work, but some stack-specific features need manual steps.",
            CapabilityBadgeTone.Warning),
        FeatureCapability.Hidden => new(
            capability,
            "Hidden",
            "This feature is hidden from navigation. Its configuration is kept and it can be restored at any time.",
            CapabilityBadgeTone.Muted),
        _ => new(
            capability,
            "Unavailable",
            "This feature is not available for the current project, device, or machine.",
            CapabilityBadgeTone.Muted),
    };
}
