using System.Text.RegularExpressions;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects Flutter projects from pubspec.yaml and an Android host folder. The
/// detector never invokes the Flutter CLI.
/// </summary>
public sealed partial class FlutterProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "flutter";

    /// <inheritdoc />
    public int Priority => 15;

    /// <inheritdoc />
    public async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = context.RootPath;
        var pubspec = Path.Combine(root, "pubspec.yaml");
        if (!File.Exists(pubspec))
        {
            return null;
        }

        var pubspecText = await ProjectSource.ReadTextAsync(pubspec, cancellationToken).ConfigureAwait(false);
        if (pubspecText is null || !IsFlutterProject(pubspecText))
        {
            return null;
        }

        var host = await AndroidHostProbe.ProbeAsync(root, "android", cancellationToken).ConfigureAwait(false);
        if (!host.HasAndroidHost)
        {
            return null;
        }

        var evidence = new List<DetectionEvidence>
        {
            new()
            {
                Id = "flutter-pubspec",
                Source = "pubspec.yaml",
                Detail = "pubspec.yaml declares the Flutter SDK.",
                Confidence = DetectionConfidence.High,
            },
            new()
            {
                Id = "flutter-android-folder",
                Source = "android",
                Detail = "The android folder hosts the Flutter application.",
                Confidence = DetectionConfidence.High,
            },
        };
        var metadataPath = Path.Combine(root, ".metadata");
        if (File.Exists(metadataPath))
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "flutter-metadata",
                Source = ".metadata",
                Detail = "The Flutter tool generated .metadata for this project.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        var buildFile = host.BuildFile;
        if (host.ApplicationId is not null || host.Namespace is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "flutter-application-id",
                Source = host.BuildFileEvidenceSource("android/app"),
                Detail = $"The Android host declares application ID {host.GradleApplicationId ?? "unknown"}.",
                Snippet = host.GradleApplicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (host.LauncherActivity is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "flutter-launcher-activity",
                Source = host.ManifestSource,
                Detail = $"The launcher activity is {host.LauncherActivity}.",
                Snippet = host.LauncherActivity,
                Confidence = DetectionConfidence.Medium,
            });
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = ParsePubspecName(pubspecText) ?? new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
            Kind = ProjectKind.Flutter,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.Flutter),
            Confidence = buildFile is not null && host.GradleApplicationId is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = host.GradleApplicationId,
            Namespace = host.Namespace,
            LauncherActivity = host.LauncherActivity,
            ApplicationLabel = host.ApplicationLabel,
            FlutterEnvironment = ParseEnvironmentSdk(pubspecText),
            ApplicationModule = "android/app",
            Capabilities = ProjectCapability.Build |
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
            Variants = FlutterAdapter.CreateVariants(),
            Evidence = evidence,
        };

        if (buildFile is null)
        {
            project.Warnings.Add("The android/app Gradle build file was not found. The application ID may need to be set manually.");
        }

        project.RunConfigurations = FlutterAdapter.CreateDefaultRunConfigurations(project);
        return new ProjectDetectionResult { Project = project };
    }

    private static bool IsFlutterProject(string pubspecText) =>
        pubspecText.Contains("sdk: flutter", StringComparison.OrdinalIgnoreCase) ||
        FlutterSectionRegex().IsMatch(pubspecText);

    private static string? ParsePubspecName(string text) => ProjectSource.MatchValue(PubspecNameRegex(), text);

    private static string? ParseEnvironmentSdk(string text)
    {
        var inEnvironment = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var indented = line.Length > 0 && char.IsWhiteSpace(line[0]);
            if (!indented)
            {
                inEnvironment = line.TrimStart().StartsWith("environment:", StringComparison.Ordinal);
                continue;
            }

            if (!inEnvironment)
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("sdk:", StringComparison.Ordinal))
            {
                return trimmed[4..].Trim().Trim('"', '\'').Trim();
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?m)^flutter:\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FlutterSectionRegex();

    [GeneratedRegex(@"(?m)^name:\s*(?<value>[^\s#]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PubspecNameRegex();
}
