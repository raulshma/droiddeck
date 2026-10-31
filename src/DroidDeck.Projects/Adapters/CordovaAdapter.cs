using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Describes the Cordova workflows DroidDeck supports.</summary>
public static class CordovaAdapter
{
    /// <summary>Gets the generated Android platform folder inside a Cordova project.</summary>
    public const string AndroidHostFolder = "platforms/android";

    /// <summary>Gets the folder the generated Android platform writes APKs to, relative to the project root.</summary>
    public const string ArtifactFolder = "platforms/android/app/build/outputs/apk";

    internal static WebHybridAdapter Shared { get; } = new(new WebHybridProfile(
        ProjectKind.Cordova,
        RunConfigurationKind.Cordova,
        "cordova",
        AndroidHostFolder,
        ArtifactFolder,
        ["debug", "release"],
        ["cordova", "prepare", "android"]));

    /// <summary>Gets the supported build modes in display order.</summary>
    public static IReadOnlyList<string> Modes => Shared.Modes;

    /// <summary>Returns the APK glob for a build mode.</summary>
    public static string GetArtifactGlob(string? mode) => Shared.GetArtifactGlob(mode);

    /// <summary>Returns the Gradle task that builds a mode in the generated platform.</summary>
    public static string GetBuildTask(string? mode) => WebHybridAdapter.GetBuildTask(mode);

    /// <summary>Normalizes a mode name, falling back to debug.</summary>
    public static string NormalizeMode(string? mode) => WebHybridAdapter.NormalizeMode(mode);

    /// <summary>Builds the argument list for the Cordova prepare command served through npx.</summary>
    public static IReadOnlyList<string> CreatePrepareArguments() => Shared.CreatePrepareArguments();

    /// <summary>Creates the debug and release run configurations.</summary>
    public static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project) =>
        Shared.CreateDefaultRunConfigurations(project);

    /// <summary>Creates the debug and release variants shown in the project UI.</summary>
    public static List<ProjectVariant> CreateVariants() => Shared.CreateVariants();
}
