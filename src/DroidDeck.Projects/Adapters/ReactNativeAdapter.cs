using System.Globalization;
using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Describes the React Native build modes DroidDeck supports.</summary>
public static class ReactNativeAdapter
{
    /// <summary>Gets the supported build modes in display order.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["debug", "release"];

    /// <summary>Gets the Gradle host folder inside a React Native project.</summary>
    public const string AndroidHostFolder = "android";

    /// <summary>Gets the folder the Android host writes APKs to, relative to the project root.</summary>
    public const string ArtifactFolder = "android/app/build/outputs/apk";

    /// <summary>Gets the default Metro development server port.</summary>
    public const int DefaultMetroPort = ReactNativeProjectInfo.DefaultMetroPort;

    /// <summary>Returns the APK glob for a build mode.</summary>
    public static string GetArtifactGlob(string? mode) =>
        $"{ArtifactFolder}/{NormalizeMode(mode)}/*.apk";

    /// <summary>Returns the Gradle task that builds a mode in the android host.</summary>
    public static string GetBuildTask(string? mode)
    {
        var normalized = NormalizeMode(mode);
        return "assemble" + (char.ToUpperInvariant(normalized[0]) + normalized[1..]);
    }

    /// <summary>Normalizes a mode name, falling back to debug.</summary>
    public static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "debug";
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "release" ? normalized : "debug";
    }

    /// <summary>Builds the argument list for the Metro start command served through npx.</summary>
    public static IReadOnlyList<string> CreateMetroStartArguments(
        bool isExpo,
        int port,
        IReadOnlyList<string>? extraArguments = null)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return isExpo
            ? ["expo", "start", "--dev-client", "--port", port.ToString(CultureInfo.InvariantCulture), .. (extraArguments ?? [])]
            : ["react-native", "start", "--port", port.ToString(CultureInfo.InvariantCulture), .. (extraArguments ?? [])];
    }

    /// <summary>Creates the debug and release run configurations.</summary>
    public static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var configurations = new List<RunConfiguration>();
        foreach (var mode in Modes)
        {
            configurations.Add(new RunConfiguration
            {
                Id = $"react-native-{mode}",
                Name = char.ToUpperInvariant(mode[0]) + mode[1..],
                Kind = RunConfigurationKind.ReactNative,
                VariantName = mode,
                IsDefault = mode == "debug",
                Build = new BuildConfiguration
                {
                    Task = GetBuildTask(mode),
                    WorkingDirectory = AndroidHostFolder,
                    TimeoutSeconds = 1_200,
                },
                Artifact = new ArtifactConfiguration
                {
                    Glob = GetArtifactGlob(mode),
                    PackageName = project.ApplicationId,
                },
                Launch = new LaunchConfiguration
                {
                    PackageName = project.ApplicationId,
                    Activity = project.LauncherActivity,
                },
            });
        }

        return configurations;
    }

    /// <summary>Creates the debug and release variants shown in the project UI.</summary>
    public static List<ProjectVariant> CreateVariants()
    {
        var variants = new List<ProjectVariant>();
        foreach (var mode in Modes)
        {
            variants.Add(new ProjectVariant
            {
                Name = mode,
                BuildType = mode,
                ArtifactGlob = GetArtifactGlob(mode),
                Source = DetectionSource.Detected,
                IsDefault = mode == "debug",
            });
        }

        return variants;
    }
}
