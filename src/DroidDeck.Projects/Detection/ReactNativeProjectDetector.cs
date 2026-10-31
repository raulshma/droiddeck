using System.Text.Json;
using System.Text.RegularExpressions;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects React Native and Expo projects from package.json, the Expo config, and an Android
/// host folder. The detector never invokes Node, npm, or Metro.
/// </summary>
public sealed partial class ReactNativeProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "react-native";

    /// <inheritdoc />
    public int Priority => 12;

    /// <inheritdoc />
    public async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = context.RootPath;
        var packageJsonPath = Path.Combine(root, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }

        var packageJsonText = await ProjectSource.ReadTextAsync(packageJsonPath, cancellationToken).ConfigureAwait(false);
        if (packageJsonText is null ||
            !TryParsePackageJson(packageJsonText, out var packageName, out var isExpo, out var hasReactNative, out var hasAndroidScript))
        {
            return null;
        }

        if (!isExpo && !hasReactNative)
        {
            return null;
        }

        var host = await AndroidHostProbe.ProbeAsync(
            root,
            ReactNativeAdapter.AndroidHostFolder,
            cancellationToken).ConfigureAwait(false);
        var hasAndroidHost = host.HasAndroidHost;
        var workflow = isExpo
            ? (hasAndroidHost ? "expo-prebuild" : "expo-managed")
            : "bare";
        var managedExpo = workflow == "expo-managed";

        var evidence = new List<DetectionEvidence>
        {
            new()
            {
                Id = "react-native-package",
                Source = "package.json",
                Detail = isExpo
                    ? "package.json declares the Expo framework."
                    : "package.json declares the react-native dependency.",
                Confidence = DetectionConfidence.High,
            },
        };
        if (hasAndroidHost)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "react-native-android-folder",
                Source = ReactNativeAdapter.AndroidHostFolder,
                Detail = "The android folder hosts the React Native application.",
                Confidence = DetectionConfidence.High,
            });
        }

        var metroConfig = ProjectSource.FindFirst(root, "metro.config.js", "metro.config.mjs", "metro.config.cjs", "metro.config.ts");
        if (metroConfig is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "react-native-metro-config",
                Source = Path.GetFileName(metroConfig),
                Detail = "A Metro configuration customizes the development server.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        if (hasAndroidScript)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "react-native-android-script",
                Source = "package.json",
                Detail = "The android script in package.json runs the project on Android.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        if (isExpo)
        {
            var expoConfig = ProjectSource.FindFirst(root, "app.json", "app.config.js", "app.config.ts");
            if (expoConfig is not null)
            {
                evidence.Add(new DetectionEvidence
                {
                    Id = "react-native-expo-config",
                    Source = Path.GetFileName(expoConfig),
                    Detail = "The Expo configuration marks this as an Expo project.",
                    Confidence = DetectionConfidence.Medium,
                });
            }
        }

        var buildFile = host.BuildFile;
        if (hasAndroidHost && (host.ApplicationId is not null || host.Namespace is not null))
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "react-native-application-id",
                Source = host.BuildFileEvidenceSource("android/app"),
                Detail = $"The Android host declares application ID {host.GradleApplicationId ?? "unknown"}.",
                Snippet = host.GradleApplicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (hasAndroidHost && host.LauncherActivity is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "react-native-launcher-activity",
                Source = host.ManifestSource,
                Detail = $"The launcher activity is {host.LauncherActivity}.",
                Snippet = host.LauncherActivity,
                Confidence = DetectionConfidence.Medium,
            });
        }

        var metroPort = ParseMetroPort(await ProjectSource.ReadTextAsync(metroConfig, cancellationToken).ConfigureAwait(false))
            ?? ReactNativeAdapter.DefaultMetroPort;
        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = packageName ?? new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
            Kind = ProjectKind.ReactNative,
            KindLabel = isExpo && managedExpo ? "Expo (managed)" : ProjectModel.GetKindLabel(ProjectKind.ReactNative),
            Confidence = !managedExpo && buildFile is not null && host.GradleApplicationId is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = host.GradleApplicationId,
            Namespace = host.Namespace,
            LauncherActivity = host.LauncherActivity,
            ApplicationLabel = host.ApplicationLabel,
            ApplicationModule = hasAndroidHost ? "android/app" : null,
            ReactNativeInfo = new ReactNativeProjectInfo
            {
                IsExpo = isExpo,
                Workflow = workflow,
                MetroPort = metroPort,
            },
            Capabilities = managedExpo
                ? ProjectCapability.Limited
                : ProjectCapability.Build |
                  ProjectCapability.Install |
                  ProjectCapability.Launch |
                  ProjectCapability.HotReload |
                  ProjectCapability.MultiDevice,
            Modules = buildFile is null
                ? []
                : [
                    new ProjectModule
                    {
                        Name = "android/app",
                        Path = "android/app",
                        BuildFile = host.BuildFileModulePath,
                        Type = ProjectModuleType.Application,
                        ApplicationId = host.GradleApplicationId,
                        Namespace = host.Namespace,
                        HasAndroidTarget = true,
                    },
                ],
            Variants = managedExpo ? [] : ReactNativeAdapter.CreateVariants(),
            Evidence = evidence,
        };

        if (managedExpo)
        {
            project.Limitation =
                "This Expo project has no android folder yet, so DroidDeck cannot build or install it. " +
                "Run \"npx expo prebuild --platform android\" to generate the native host, then refresh the " +
                "project in DroidDeck. After the prebuild, builds, installs, Metro, and reload work like any " +
                "bare React Native project. Managed Expo never supports arbitrary native debugging.";
        }
        else if (buildFile is null)
        {
            project.Warnings.Add(
                "The android/app Gradle build file was not found. The application ID may need to be set manually.");
        }

        if (!managedExpo)
        {
            project.RunConfigurations = ReactNativeAdapter.CreateDefaultRunConfigurations(project);
        }

        return new ProjectDetectionResult { Project = project };
    }

    private static bool TryParsePackageJson(
        string text,
        out string? name,
        out bool isExpo,
        out bool hasReactNative,
        out bool hasAndroidScript)
    {
        name = null;
        isExpo = false;
        hasReactNative = false;
        hasAndroidScript = false;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String)
            {
                name = nameValue.GetString();
            }

            if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty("android", out var androidScript) &&
                androidScript.ValueKind == JsonValueKind.String)
            {
                hasAndroidScript = true;
            }

            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!root.TryGetProperty(section, out var dependencies) ||
                    dependencies.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (string.Equals(dependency.Name, "react-native", StringComparison.OrdinalIgnoreCase))
                    {
                        hasReactNative = true;
                    }

                    if (string.Equals(dependency.Name, "expo", StringComparison.OrdinalIgnoreCase))
                    {
                        isExpo = true;
                    }
                }
            }

            return true;
        }
    }

    private static int? ParseMetroPort(string? metroConfigText)
    {
        if (string.IsNullOrWhiteSpace(metroConfigText))
        {
            return null;
        }

        var match = MetroPortRegex().Match(metroConfigText);
        return match.Success &&
               int.TryParse(match.Groups["port"].Value, out var port) &&
               port is >= 1 and <= 65535
            ? port
            : null;
    }

    [GeneratedRegex(@"(?<!\w)port\s*:\s*(?<port>\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetroPortRegex();
}
