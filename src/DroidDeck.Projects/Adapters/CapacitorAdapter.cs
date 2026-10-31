using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Describes the Capacitor workflows DroidDeck supports.</summary>
public static class CapacitorAdapter
{
    /// <summary>Gets the folder the Capacitor CLI treats as the Android host.</summary>
    public const string AndroidHostFolder = "android";

    /// <summary>Gets the folder the Android host writes APKs to, relative to the project root.</summary>
    public const string ArtifactFolder = "android/app/build/outputs/apk";

    internal static WebHybridAdapter Shared { get; } = new(new WebHybridProfile(
        ProjectKind.Capacitor,
        RunConfigurationKind.Capacitor,
        "capacitor",
        AndroidHostFolder,
        ArtifactFolder,
        ["debug", "release"],
        ["cap", "sync", "android"]));

    /// <summary>Gets the supported build modes in display order.</summary>
    public static IReadOnlyList<string> Modes => Shared.Modes;

    /// <summary>Returns the APK glob for a build mode.</summary>
    public static string GetArtifactGlob(string? mode) => Shared.GetArtifactGlob(mode);

    /// <summary>Returns the Gradle task that builds a mode in the android host.</summary>
    public static string GetBuildTask(string? mode) => WebHybridAdapter.GetBuildTask(mode);

    /// <summary>Normalizes a mode name, falling back to debug.</summary>
    public static string NormalizeMode(string? mode) => WebHybridAdapter.NormalizeMode(mode);

    /// <summary>Builds the argument list for the Capacitor sync command served through npx.</summary>
    public static IReadOnlyList<string> CreateSyncArguments() => Shared.CreatePrepareArguments();

    /// <summary>Creates the debug and release run configurations.</summary>
    public static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project) =>
        Shared.CreateDefaultRunConfigurations(project);

    /// <summary>Creates the debug and release variants shown in the project UI.</summary>
    public static List<ProjectVariant> CreateVariants() => Shared.CreateVariants();
}
