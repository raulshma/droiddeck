using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Describes the Flutter build modes DroidDeck supports.</summary>
public static class FlutterAdapter
{
    /// <summary>Gets the supported build modes in display order.</summary>
    public static IReadOnlyList<string> Modes { get; } = ["debug", "profile", "release"];

    /// <summary>Gets the folder Flutter writes APKs to.</summary>
    public const string ArtifactFolder = "build/app/outputs/flutter-apk";

    /// <summary>Returns the APK glob for a build mode.</summary>
    public static string GetArtifactGlob(string? mode) =>
        $"{ArtifactFolder}/app-{NormalizeMode(mode)}.apk";

    /// <summary>Builds the argument list for <c>flutter build apk</c>.</summary>
    public static IReadOnlyList<string> CreateBuildArguments(
        string? mode,
        IReadOnlyList<string>? extraArguments = null) =>
        ["build", "apk", $"--{NormalizeMode(mode)}", .. (extraArguments ?? [])];

    /// <summary>Builds the argument list for an attached <c>flutter run --machine</c> session.</summary>
    public static IReadOnlyList<string> CreateRunSessionArguments(
        string deviceSerial,
        string? mode,
        IReadOnlyList<string>? extraArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        return ["run", "--machine", "-d", deviceSerial, $"--{NormalizeMode(mode)}", .. (extraArguments ?? [])];
    }

    /// <summary>Builds the argument list for <c>flutter test</c>.</summary>
    public static IReadOnlyList<string> CreateTestArguments() => ["test", "--reporter", "json"];

    /// <summary>Normalizes a mode name, falling back to debug.</summary>
    public static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "debug";
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "profile" or "release" ? normalized : "debug";
    }

    /// <summary>Creates the default debug, profile, and release run configurations.</summary>
    public static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var configurations = new List<RunConfiguration>();
        foreach (var mode in Modes)
        {
            configurations.Add(new RunConfiguration
            {
                Id = $"flutter-{mode}",
                Name = char.ToUpperInvariant(mode[0]) + mode[1..],
                Kind = RunConfigurationKind.Flutter,
                VariantName = mode,
                IsDefault = mode == "debug",
                Build = new BuildConfiguration
                {
                    Task = null,
                    TimeoutSeconds = 900,
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

    /// <summary>Creates the debug, profile, and release variants shown in the project UI.</summary>
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
