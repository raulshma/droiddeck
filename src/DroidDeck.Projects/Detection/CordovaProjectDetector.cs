using System.Text.Json;
using System.Xml.Linq;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects Apache Cordova projects from config.xml, package.json, and the generated android
/// platform. The detector never invokes Node or the Cordova CLI.
/// </summary>
public sealed class CordovaProjectDetector : IProjectDetector
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

        var configText = await ProjectSource.ReadTextAsync(configPath, cancellationToken).ConfigureAwait(false);
        if (configText is null || TryParseConfig(configText) is not { } config)
        {
            return null;
        }

        var packageJsonText = await ProjectSource.ReadTextAsync(
            Path.Combine(root, "package.json"),
            cancellationToken).ConfigureAwait(false);
        var packageName = packageJsonText is null ? null : TryParsePackageName(packageJsonText);
        var hasCordovaPackage = packageJsonText is not null && HasCordovaDependency(packageJsonText);
        var host = await AndroidHostProbe.ProbeAsync(
            root,
            CordovaAdapter.AndroidHostFolder,
            cancellationToken).ConfigureAwait(false);
        var hasAndroidHost = host.HasAndroidHost;

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

        var buildFile = host.BuildFile;
        var applicationId = config.WidgetId ?? host.GradleApplicationId;
        if (applicationId is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "cordova-application-id",
                Source = config.WidgetId is not null
                    ? "config.xml"
                    : host.BuildFileEvidenceSource(CordovaAdapter.AndroidHostFolder),
                Detail = $"The project declares application ID {applicationId}.",
                Snippet = applicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (host.LauncherActivity is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "cordova-launcher-activity",
                Source = host.ManifestSource,
                Detail = $"The launcher activity is {host.LauncherActivity}.",
                Snippet = host.LauncherActivity,
                Confidence = DetectionConfidence.Medium,
            });
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = config.Name ?? host.ApplicationLabel ?? packageName ?? new DirectoryInfo(root).Name,
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
            LauncherActivity = host.LauncherActivity,
            ApplicationLabel = host.ApplicationLabel ?? config.Name,
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
                        BuildFile = host.BuildFileModulePath,
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

    private sealed record ParsedConfig(string? WidgetId, string? Name);
}
