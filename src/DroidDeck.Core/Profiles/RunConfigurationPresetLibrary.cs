using DroidDeck.Core.Features;
using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Profiles;

/// <summary>A run-configuration preset offered when adding a project.</summary>
/// <param name="Id">The stable preset identifier.</param>
/// <param name="Name">The display name.</param>
/// <param name="Description">A short explanation of the workflow.</param>
/// <param name="Kind">The run-configuration kind the preset produces.</param>
/// <param name="ProjectKinds">Project kinds the preset is suggested for.</param>
public sealed record RunConfigurationPreset(
    string Id,
    string Name,
    string Description,
    RunConfigurationKind Kind,
    IReadOnlyList<ProjectKind> ProjectKinds);

/// <summary>
/// Creates sensible run configurations for the built-in stacks in a few steps.
/// Presets only seed values; the normal validator and editor remain authoritative.
/// </summary>
public static class RunConfigurationPresetLibrary
{
    /// <summary>Gets every built-in preset.</summary>
    public static IReadOnlyList<RunConfigurationPreset> Default { get; } =
    [
        new(
            "gradle-debug",
            "Android Gradle debug",
            "Build assembleDebug, install, and launch the default activity.",
            RunConfigurationKind.Gradle,
            [ProjectKind.AndroidGradle, ProjectKind.KotlinMultiplatform, ProjectKind.Unity]),
        new(
            "flutter-debug",
            "Flutter debug",
            "Run flutter build apk --debug, install, and attach a Flutter session.",
            RunConfigurationKind.Flutter,
            [ProjectKind.Flutter]),
        new(
            "react-native-debug",
            "React Native debug",
            "Start Metro, build the Android host, install, and launch.",
            RunConfigurationKind.ReactNative,
            [ProjectKind.ReactNative]),
        new(
            "maui-debug",
            "MAUI Android debug",
            "Publish the .NET Android target, install, and launch.",
            RunConfigurationKind.Maui,
            [ProjectKind.Maui]),
        new(
            "apk-install",
            "Install APK",
            "Install an existing APK without building.",
            RunConfigurationKind.Apk,
            [ProjectKind.ApkOnly, ProjectKind.Unity]),
        new(
            "custom-steps",
            "Custom steps",
            "Drive the run with user-defined build and launch commands.",
            RunConfigurationKind.Custom,
            [ProjectKind.Custom]),
    ];

    /// <summary>Gets the presets suggested for a project kind.</summary>
    public static IReadOnlyList<RunConfigurationPreset> Suggest(ProjectKind kind) =>
        Default.Where(preset => preset.ProjectKinds.Contains(kind)).ToArray();

    /// <summary>Finds a preset by identifier.</summary>
    public static RunConfigurationPreset? Find(string presetId) =>
        Default.FirstOrDefault(preset =>
            string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates a run configuration from a preset and an optional project.</summary>
    public static RunConfiguration Create(string presetId, ProjectModel? project = null)
    {
        var preset = Find(presetId)
            ?? throw new ArgumentException($"'{presetId}' is not a known run-configuration preset.", nameof(presetId));

        var configuration = new RunConfiguration
        {
            Id = $"{preset.Id}-{Guid.NewGuid():N}"[..Math.Min(64, preset.Id.Length + 33)],
            Name = project is null ? preset.Name : $"{project.Name} · {preset.Name}",
            Kind = preset.Kind,
            IsDefault = true,
        };

        var variant = project?.Variants.FirstOrDefault(static variant => variant.IsDefault);
        if (project is not null)
        {
            var packageName = variant?.ApplicationId ?? project.ApplicationId;
            configuration.Artifact.PackageName = packageName;
            configuration.Launch.PackageName = packageName;
            configuration.Launch.Activity = project.LauncherActivity;
            configuration.ModuleName = variant?.ModuleName ?? project.Modules
                .FirstOrDefault(static module => module.Type == ProjectModuleType.Application)
                ?.Name;
            configuration.VariantName = variant?.Name;
        }

        switch (preset.Kind)
        {
            case RunConfigurationKind.Gradle:
                configuration.Build.Task = !string.IsNullOrWhiteSpace(variant?.BuildTask)
                    ? variant.BuildTask
                    : BuildTask(configuration.ModuleName, "assembleDebug");
                configuration.Artifact.Glob = !string.IsNullOrWhiteSpace(variant?.ArtifactGlob)
                    ? variant.ArtifactGlob
                    : "**/build/outputs/apk/debug/*.apk";
                configuration.Screen.Mode = ScreenMode.Embedded;
                break;

            case RunConfigurationKind.Flutter:
                configuration.Build.Task = "flutter build apk --debug";
                configuration.Build.WorkingDirectory = ".";
                configuration.Artifact.Glob = "build/app/outputs/flutter-apk/app-debug.apk";
                configuration.Steps.Add(new RunStepDefinition
                {
                    Id = "flutter-attach",
                    Name = "Attach Flutter session",
                    Kind = RunStepKind.HotReload,
                    FailurePolicy = StepFailurePolicy.Continue,
                });
                break;

            case RunConfigurationKind.ReactNative:
                configuration.Build.Task = "assembleDebug";
                configuration.Build.WorkingDirectory = "android";
                configuration.Artifact.Glob = "android/app/build/outputs/apk/debug/*.apk";
                configuration.Steps.Add(new RunStepDefinition
                {
                    Id = "metro",
                    Name = "Start Metro",
                    Kind = RunStepKind.Metro,
                    FailurePolicy = StepFailurePolicy.Continue,
                });
                break;

            case RunConfigurationKind.Maui:
                configuration.Build.Task = project?.Modules
                    .FirstOrDefault(static module => module.Type == ProjectModuleType.Application)
                    ?.BuildFile ?? "dotnet publish";
                configuration.Build.TargetFramework = "net10.0-android";
                configuration.Build.Arguments = ["-c", "Debug"];
                configuration.Artifact.Glob = "**/bin/Debug/net10.0-android/**/*-Signed.apk";
                break;

            case RunConfigurationKind.Apk:
                configuration.Build.Skip = true;
                configuration.Artifact.Glob = project?.ApkFiles.FirstOrDefault()?.Path ?? "**/*.apk";
                break;

            case RunConfigurationKind.Custom:
                configuration.Build.Skip = true;
                configuration.Artifact.Glob = "**/*.apk";
                configuration.Steps.Add(new RunStepDefinition
                {
                    Id = "build",
                    Name = "Build",
                    Kind = RunStepKind.Custom,
                    Command = "echo Replace this step with your build command",
                });
                break;

            default:
                break;
        }

        return configuration;
    }

    /// <summary>Maps a project kind to the capability badge shown in the UI.</summary>
    public static FeatureCapability GetCapability(ProjectKind kind) => kind switch
    {
        ProjectKind.AndroidGradle or ProjectKind.KotlinMultiplatform => FeatureCapability.Full,
        ProjectKind.Flutter or ProjectKind.ReactNative or ProjectKind.Maui or ProjectKind.Unity =>
            FeatureCapability.Limited,
        ProjectKind.Capacitor or ProjectKind.Cordova => FeatureCapability.Cli,
        ProjectKind.ApkOnly => FeatureCapability.ApkOnly,
        _ => FeatureCapability.Custom,
    };

    private static string BuildTask(string? moduleName, string task)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return task;
        }

        var normalized = moduleName.StartsWith(':') ? moduleName : ":" + moduleName;
        return $"{normalized}:{task}";
    }
}
