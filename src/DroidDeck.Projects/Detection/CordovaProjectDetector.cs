using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects Apache Cordova projects from config.xml, package.json, and the generated android
/// platform. The detector never invokes Node or the Cordova CLI.
/// </summary>
public sealed partial class CordovaProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "cordova";

    /// <inheritdoc />
    public int Priority => 13;

    /// <inheritdoc />
    public async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = context.RootPath;
        var configPath = Path.Combine(root, "config.xml");
        if (!File.Exists(configPath))
        {
            return null;
        }

        var configText = await ReadTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        if (configText is null || TryParseConfig(configText) is not { } config)
        {
            return null;
        }

        var packageJsonText = await ReadTextAsync(
            Path.Combine(root, "package.json"),
            cancellationToken).ConfigureAwait(false);
        var packageName = packageJsonText is null ? null : TryParsePackageName(packageJsonText);
        var hasCordovaPackage = packageJsonText is not null && HasCordovaDependency(packageJsonText);
        var androidFolder = Path.Combine(root, CordovaAdapter.AndroidHostFolder);
        var hasAndroidHost = Directory.Exists(androidFolder);

        var evidence = new List<DetectionEvidence>
        {
            new()
            {
                Id = "cordova-config",
                Source = "config.xml",
                Detail = "config.xml declares the Cordova widget and application identity.",
                Snippet = config.WidgetId,
                Confidence = DetectionConfidence.High,
            },
        };
        if (hasCordovaPackage)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "cordova-package",
                Source = "package.json",
                Detail = "package.json declares the Cordova CLI or the Android platform package.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        if (hasAndroidHost)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "cordova-android-folder",
                Source = CordovaAdapter.AndroidHostFolder,
                Detail = "The platforms/android folder hosts the generated Cordova application.",
                Confidence = DetectionConfidence.High,
            });
        }

        string? buildFile = null;
        string? gradleApplicationId = null;
        ManifestInfo manifest = new();
        if (hasAndroidHost)
        {
            buildFile = FindFirst(
                Path.Combine(androidFolder, "app"),
                "build.gradle.kts",
                "build.gradle");
            var buildText = await ReadTextAsync(buildFile, cancellationToken).ConfigureAwait(false);
            gradleApplicationId = MatchValue(ApplicationIdRegex(), buildText)
                ?? MatchValue(NamespaceRegex(), buildText);
            var manifestPath = Path.Combine(androidFolder, "app", "src", "main", "AndroidManifest.xml");
            if (File.Exists(manifestPath))
            {
                manifest = await AndroidManifestParser.ParseAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            }
        }

        var applicationId = config.WidgetId ?? gradleApplicationId;
        if (applicationId is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "cordova-application-id",
                Source = config.WidgetId is not null
                    ? "config.xml"
                    : buildFile is null ? CordovaAdapter.AndroidHostFolder : Path.GetRelativePath(root, buildFile),
                Detail = $"The project declares application ID {applicationId}.",
                Snippet = applicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (manifest.LauncherActivity is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "cordova-launcher-activity",
                Source = "platforms/android/app/src/main/AndroidManifest.xml",
                Detail = $"The launcher activity is {manifest.LauncherActivity}.",
                Snippet = manifest.LauncherActivity,
                Confidence = DetectionConfidence.Medium,
            });
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = config.Name ?? manifest.ApplicationLabel ?? packageName ?? new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
            Kind = ProjectKind.Cordova,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.Cordova),
            Confidence = hasAndroidHost && applicationId is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = applicationId,
            LauncherActivity = manifest.LauncherActivity,
            ApplicationLabel = manifest.ApplicationLabel ?? config.Name,
            ApplicationModule = hasAndroidHost ? "platforms/android/app" : null,
            CordovaInfo = new CordovaProjectInfo
            {
                ConfigFile = "config.xml",
                HasAndroidHost = hasAndroidHost,
            },
            Modules = hasAndroidHost
                ? [
                    new ProjectModule
                    {
                        Name = "platforms/android/app",
                        Path = "platforms/android/app",
                        BuildFile = buildFile is null ? null : Path.GetRelativePath(root, buildFile).Replace('\\', '/'),
                        Type = ProjectModuleType.Application,
                        ApplicationId = applicationId,
                        HasAndroidTarget = true,
                    },
                ]
                : [],
            Evidence = evidence,
        };

        if (!hasAndroidHost)
        {
            project.Capabilities = ProjectCapability.Limited;
            project.Limitation =
                "This Cordova project has no platforms/android folder yet, so DroidDeck cannot build or " +
                "install it. Run \"npx cordova platform add android\" to generate the platform, then refresh " +
                "the project in DroidDeck. After the platform exists, DroidDeck runs \"npx cordova prepare " +
                "android\" and the Gradle wrapper; it does not wrap \"cordova run\", so live reload and plugin " +
                "hooks stay inside the CLI.";
            return new ProjectDetectionResult { Project = project };
        }

        project.Capabilities = ProjectCapability.Build |
                               ProjectCapability.Install |
                               ProjectCapability.Launch |
                               ProjectCapability.Variants |
                               ProjectCapability.MultiDevice;
        project.Variants = CordovaAdapter.CreateVariants();
        project.RunConfigurations = CordovaAdapter.CreateDefaultRunConfigurations(project);
        if (applicationId is null)
        {
            project.Warnings.Add(
                "config.xml and the Android platform do not declare an application ID. Set the launch package name in the run configuration.");
        }

        if (buildFile is null)
        {
            project.Warnings.Add(
                "The platforms/android/app Gradle build file was not found. The application ID may need to be set manually.");
        }

        return new ProjectDetectionResult { Project = project };
    }

    private static ParsedConfig? TryParseConfig(string text)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var widget = document.Root;
        if (widget is null || widget.Name.LocalName != "widget")
        {
            return null;
        }

        var name = widget
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "name")
            ?.Value
            .Trim();
        return new ParsedConfig(
            string.IsNullOrWhiteSpace(widget.Attribute("id")?.Value) ? null : widget.Attribute("id")!.Value.Trim(),
            string.IsNullOrWhiteSpace(name) ? null : name);
    }

    private static string? TryParsePackageName(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("name", out var name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasCordovaDependency(string packageJsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(packageJsonText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
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
                    if (dependency.Name.StartsWith("cordova", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
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

    private sealed record ParsedConfig(string? WidgetId, string? Name);

    [GeneratedRegex(@"applicationId\s*=?\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApplicationIdRegex();

    [GeneratedRegex(@"namespace\s*=?\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamespaceRegex();
}
