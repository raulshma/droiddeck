using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Describes the Unity workflow DroidDeck supports: open, build elsewhere, install the APK.</summary>
public static class UnityAdapter
{
    /// <summary>Gets the default folder Unity writes Android builds to.</summary>
    public const string BuildFolder = "Builds";

    /// <summary>Gets the file that hosts the custom Editor build method.</summary>
    public const string BuildTemplatePath = "Assets/Editor/DroidDeckAndroidBuild.cs";

    /// <summary>Gets the fully qualified batch-mode build method.</summary>
    public const string BuildMethod = "DroidDeck.Unity.AndroidBuild.Build";

    /// <summary>
    /// Gets the Editor script template. Copy it into the project, then run Unity with
    /// <c>-executeMethod DroidDeck.Unity.AndroidBuild.Build</c> as a custom build step.
    /// </summary>
    public const string BuildTemplate = """
        #if UNITY_EDITOR
        using System.IO;
        using System.Linq;
        using UnityEditor;
        using UnityEditor.Build.Reporting;
        using UnityEngine;

        namespace DroidDeck.Unity
        {
            /// <summary>
            /// Batch-mode Android build entry point for DroidDeck custom steps. Run with:
            /// Unity.exe -quit -batchmode -projectPath "<project>" -executeMethod DroidDeck.Unity.AndroidBuild.Build -logFile -
            /// </summary>
            public static class AndroidBuild
            {
                private const string OutputPath = "Builds/Android/DroidDeck.apk";

                public static void Build()
                {
                    var scenes = EditorBuildSettings.scenes
                        .Where(scene => scene.enabled)
                        .Select(scene => scene.path)
                        .ToArray();
                    if (scenes.Length == 0)
                    {
                        Debug.LogError("No enabled scenes are configured in Build Settings.");
                        EditorApplication.Exit(1);
                        return;
                    }

                    var outputDirectory = Path.GetDirectoryName(OutputPath);
                    if (!string.IsNullOrEmpty(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                    }

                    var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = scenes,
                        locationPathName = OutputPath,
                        target = BuildTarget.Android,
                        options = BuildOptions.None,
                    });

                    if (report.summary.result != BuildResult.Succeeded)
                    {
                        Debug.LogError($"Android build failed: {report.summary.result}");
                        EditorApplication.Exit(1);
                    }
                }
            }
        }
        #endif
        """;

    /// <summary>Returns the APK glob, optionally narrowed to a detected build folder.</summary>
    public static string GetArtifactGlob(string? relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return $"{BuildFolder}/**/*.apk";
        }

        var normalized = relativeDirectory.Replace('\\', '/').TrimEnd('/');
        return $"{normalized}/*.apk";
    }

    /// <summary>Creates the configuration that installs and launches an APK built in Unity.</summary>
    public static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return
        [
            new RunConfiguration
            {
                Id = "unity-apk",
                Name = "Install built APK",
                Kind = RunConfigurationKind.Apk,
                IsDefault = true,
                Build = new BuildConfiguration { Skip = true },
                Artifact = new ArtifactConfiguration
                {
                    Glob = GetArtifactGlob(null),
                    PackageName = project.ApplicationId,
                },
                Launch = new LaunchConfiguration
                {
                    PackageName = project.ApplicationId,
                    Activity = project.LauncherActivity,
                },
            },
        ];
    }
}
