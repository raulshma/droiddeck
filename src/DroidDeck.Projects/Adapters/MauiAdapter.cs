using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Describes the .NET MAUI build modes DroidDeck supports.</summary>
public static class MauiAdapter
{
    /// <summary>Gets the build configurations in display order.</summary>
    public static IReadOnlyList<string> Configurations { get; } = ["Debug", "Release"];

    /// <summary>Gets the target framework assumed when a project does not declare one.</summary>
    public const string DefaultTargetFramework = "net10.0-android";

    private const string AndroidTargetFrameworkMarker = "-android";

    /// <summary>Returns whether a target framework targets Android.</summary>
    public static bool IsAndroidTargetFramework(string? targetFramework) =>
        !string.IsNullOrWhiteSpace(targetFramework) &&
        targetFramework.Contains(AndroidTargetFrameworkMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalizes a configuration name, falling back to Debug.</summary>
    public static string NormalizeConfiguration(string? configuration) =>
        string.Equals(configuration?.Trim(), "release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

    /// <summary>Normalizes a target framework, falling back to the default Android framework.</summary>
    public static string NormalizeTargetFramework(string? targetFramework) =>
        string.IsNullOrWhiteSpace(targetFramework) ? DefaultTargetFramework : targetFramework.Trim();

    /// <summary>Builds the argument list for <c>dotnet build</c>.</summary>
    public static IReadOnlyList<string> CreateBuildArguments(
        string projectFile,
        string? configuration,
        string? targetFramework,
        IReadOnlyList<string>? extraArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);
        return
        [
            "build",
            projectFile,
            "-c",
            NormalizeConfiguration(configuration),
            "-f",
            NormalizeTargetFramework(targetFramework),
            .. (extraArguments ?? []),
        ];
    }

    /// <summary>Returns the glob for the signed APK the MAUI Android build produces.</summary>
    public static string GetArtifactGlob(string? projectDirectory, string? configuration, string? targetFramework)
    {
        var folder = string.IsNullOrWhiteSpace(projectDirectory)
            ? string.Empty
            : projectDirectory.Replace('\\', '/').Trim('/') + "/";
        return $"{folder}bin/{NormalizeConfiguration(configuration)}/" +
               $"{NormalizeTargetFramework(targetFramework)}/**/*-Signed.apk";
    }

    /// <summary>Creates a run configuration for every build configuration and Android target framework.</summary>
    public static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project, MauiProjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(info);
        var configurations = new List<RunConfiguration>();
        var projectDirectory = GetProjectDirectory(info.ProjectFile);
        var projectFile = string.IsNullOrWhiteSpace(info.ProjectFile)
            ? $"{project.Name}.csproj"
            : info.ProjectFile;
        var projectFileArgument = Path.GetFileName(projectFile.Replace('\\', '/'));
        var frameworks = info.AndroidTargetFrameworks;
        var multipleFrameworks = frameworks.Count > 1;
        var isFirst = true;
        foreach (var configuration in Configurations)
        {
            foreach (var framework in frameworks)
            {
                configurations.Add(new RunConfiguration
                {
                    Id = $"maui-{configuration.ToLowerInvariant()}-{Slug(framework)}",
                    Name = multipleFrameworks ? $"{configuration} ({framework})" : configuration,
                    Kind = RunConfigurationKind.Maui,
                    VariantName = configuration,
                    IsDefault = isFirst,
                    Build = new BuildConfiguration
                    {
                        Task = projectFileArgument,
                        WorkingDirectory = projectDirectory,
                        TargetFramework = framework,
                        TimeoutSeconds = 1_800,
                    },
                    Artifact = new ArtifactConfiguration
                    {
                        Glob = GetArtifactGlob(projectDirectory, configuration, framework),
                        PackageName = project.ApplicationId,
                    },
                    Launch = new LaunchConfiguration
                    {
                        PackageName = project.ApplicationId,
                        Activity = project.LauncherActivity,
                    },
                });
                isFirst = false;
            }
        }

        return configurations;
    }

    /// <summary>Creates the build configuration and Android target framework variants shown in the project UI.</summary>
    public static List<ProjectVariant> CreateVariants(MauiProjectInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var variants = new List<ProjectVariant>();
        var projectDirectory = GetProjectDirectory(info.ProjectFile);
        var multipleFrameworks = info.AndroidTargetFrameworks.Count > 1;
        var isFirst = true;
        foreach (var configuration in Configurations)
        {
            foreach (var framework in info.AndroidTargetFrameworks)
            {
                variants.Add(new ProjectVariant
                {
                    Name = multipleFrameworks ? $"{configuration} ({framework})" : configuration,
                    BuildType = configuration,
                    ArtifactGlob = GetArtifactGlob(projectDirectory, configuration, framework),
                    Source = DetectionSource.Detected,
                    IsDefault = isFirst,
                });
                isFirst = false;
            }
        }

        return variants;
    }

    private static string? GetProjectDirectory(string? projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(projectFile.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(directory) || directory == "."
            ? null
            : directory.Replace('\\', '/');
    }

    private static string Slug(string value)
    {
        var characters = value
            .ToLowerInvariant()
            .Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        return string.Join(
            '-',
            new string(characters).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}
