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
