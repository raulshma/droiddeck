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
        var androidFolder = Path.Combine(root, "android");
        if (!File.Exists(pubspec) || !Directory.Exists(androidFolder))
        {
            return null;
        }

        var pubspecText = await ReadTextAsync(pubspec, cancellationToken).ConfigureAwait(false);
        if (pubspecText is null || !IsFlutterProject(pubspecText))
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

        var buildFile = FindFirst(
            Path.Combine(androidFolder, "app"),
            "build.gradle.kts",
            "build.gradle");
        var buildText = await ReadTextAsync(buildFile, cancellationToken).ConfigureAwait(false);
        var applicationId = MatchValue(ApplicationIdRegex(), buildText);
        var namespaceName = MatchValue(NamespaceRegex(), buildText);
        var applicationIdSource = applicationId ?? namespaceName;
        if (applicationIdSource is not null || namespaceName is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "flutter-application-id",
                Source = buildFile is null ? "android/app" : Path.GetRelativePath(root, buildFile),
                Detail = $"The Android host declares application ID {applicationIdSource ?? "unknown"}.",
                Snippet = applicationIdSource,
                Confidence = DetectionConfidence.High,
            });
        }

        var manifestPath = Path.Combine(androidFolder, "app", "src", "main", "AndroidManifest.xml");
        var manifest = File.Exists(manifestPath)
            ? await AndroidManifestParser.ParseAsync(manifestPath, cancellationToken).ConfigureAwait(false)
            : new ManifestInfo();
        if (manifest.LauncherActivity is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "flutter-launcher-activity",
                Source = "android/app/src/main/AndroidManifest.xml",
                Detail = $"The launcher activity is {manifest.LauncherActivity}.",
                Snippet = manifest.LauncherActivity,
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
            Confidence = buildFile is not null && applicationIdSource is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = applicationIdSource,
            Namespace = namespaceName,
            LauncherActivity = manifest.LauncherActivity,
            ApplicationLabel = manifest.ApplicationLabel,
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
                        BuildFile = Path.GetRelativePath(root, buildFile).Replace('\\', '/'),
                        Type = ProjectModuleType.Application,
                        ApplicationId = applicationIdSource,
                        Namespace = namespaceName,
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

    private static string? ParsePubspecName(string text) => MatchValue(PubspecNameRegex(), text);

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

    private static string? MatchValue(Regex regex, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = regex.Match(text);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? FindFirst(string directory, params string[] names)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<string?> ReadTextAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?m)^flutter:\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FlutterSectionRegex();

    [GeneratedRegex(@"(?m)^name:\s*(?<value>[^\s#]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PubspecNameRegex();

    [GeneratedRegex(@"applicationId\s*=?\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApplicationIdRegex();

    [GeneratedRegex(@"namespace\s*=?\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamespaceRegex();
}
