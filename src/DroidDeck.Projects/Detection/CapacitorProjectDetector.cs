using System.Text.Json;
using System.Text.RegularExpressions;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects Ionic Capacitor projects from package.json, the Capacitor config, and the generated
/// android host. The detector never invokes Node or the Capacitor CLI.
/// </summary>
public sealed partial class CapacitorProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "capacitor";

    /// <inheritdoc />
    public int Priority => 13;

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
            !TryParsePackageJson(packageJsonText, out var packageName) ||
            !HasCapacitorDependency(packageJsonText))
        {
            return null;
        }

        var configFile = ProjectSource.FindFirst(root, "capacitor.config.ts", "capacitor.config.js", "capacitor.config.mjs", "capacitor.config.cjs", "capacitor.config.json");
        var configText = await ProjectSource.ReadTextAsync(configFile, cancellationToken).ConfigureAwait(false);
        var configAppId = ProjectSource.MatchValue(AppIdRegex(), configText);
        var configAppName = ProjectSource.MatchValue(AppNameRegex(), configText);
        var host = await AndroidHostProbe.ProbeAsync(
            root,
            CapacitorAdapter.AndroidHostFolder,
            cancellationToken).ConfigureAwait(false);
        var hasAndroidHost = host.HasAndroidHost;

        var evidence = new List<DetectionEvidence>
        {
            new()
            {
                Id = "capacitor-package",
                Source = "package.json",
                Detail = "package.json declares a Capacitor dependency.",
                Confidence = DetectionConfidence.High,
            },
        };
        if (configFile is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "capacitor-config",
                Source = Path.GetFileName(configFile),
                Detail = "The Capacitor configuration declares the application identity.",
                Snippet = configAppId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (hasAndroidHost)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "capacitor-android-folder",
                Source = CapacitorAdapter.AndroidHostFolder,
                Detail = "The android folder hosts the generated Capacitor application.",
                Confidence = DetectionConfidence.High,
            });
        }

        var buildFile = host.BuildFile;
        var applicationId = configAppId ?? host.GradleApplicationId;
        if (applicationId is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "capacitor-application-id",
                Source = configAppId is not null
                    ? Path.GetFileName(configFile!)
                    : host.BuildFileEvidenceSource(CapacitorAdapter.AndroidHostFolder),
                Detail = $"The project declares application ID {applicationId}.",
                Snippet = applicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (host.LauncherActivity is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "capacitor-launcher-activity",
                Source = host.ManifestSource,
                Detail = $"The launcher activity is {host.LauncherActivity}.",
                Snippet = host.LauncherActivity,
                Confidence = DetectionConfidence.Medium,
            });
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = configAppName ?? host.ApplicationLabel ?? packageName ?? new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
            Kind = ProjectKind.Capacitor,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.Capacitor),
            Confidence = hasAndroidHost && applicationId is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = applicationId,
            LauncherActivity = host.LauncherActivity,
            ApplicationLabel = host.ApplicationLabel ?? configAppName,
            ApplicationModule = hasAndroidHost ? "android/app" : null,
            CapacitorInfo = new CapacitorProjectInfo
            {
                ConfigFile = configFile is null ? null : Path.GetFileName(configFile),
                HasAndroidHost = hasAndroidHost,
            },
            Modules = hasAndroidHost
                ? [
                    new ProjectModule
                    {
                        Name = "android/app",
                        Path = "android/app",
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
                "This Capacitor project has no android folder yet, so DroidDeck cannot build or install it. " +
                "Run \"npx cap add android\" to generate the host, then refresh the project in DroidDeck. " +
                "After the host exists, DroidDeck runs \"npx cap sync android\" and the Gradle wrapper; it " +
                "does not wrap \"npx cap run\", so live reload and platform-specific plugins stay inside the CLI.";
            return new ProjectDetectionResult { Project = project };
        }

        project.Capabilities = ProjectCapability.Build |
                               ProjectCapability.Install |
                               ProjectCapability.Launch |
                               ProjectCapability.Variants |
                               ProjectCapability.MultiDevice;
        project.Variants = CapacitorAdapter.CreateVariants();
        project.RunConfigurations = CapacitorAdapter.CreateDefaultRunConfigurations(project);
        if (applicationId is null)
        {
            project.Warnings.Add(
                "The Capacitor config and the android host do not declare an application ID. Set the launch package name in the run configuration.");
        }

        if (buildFile is null)
        {
            project.Warnings.Add(
                "The android/app Gradle build file was not found. The application ID may need to be set manually.");
        }

        return new ProjectDetectionResult { Project = project };
    }

    private static bool TryParsePackageJson(string text, out string? name)
    {
        name = null;
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

            return true;
        }
    }

    private static bool HasCapacitorDependency(string packageJsonText)
    {
        try
        {
            using var document = JsonDocument.Parse(packageJsonText);
            var root = document.RootElement;
            foreach (var section in new[] { "dependencies", "devDependencies" })
            {
                if (!root.TryGetProperty(section, out var dependencies) ||
                    dependencies.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var dependency in dependencies.EnumerateObject())
                {
                    if (dependency.Name.StartsWith("@capacitor/", StringComparison.OrdinalIgnoreCase))
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

    [GeneratedRegex(@"\bappId[""']?\s*:\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppIdRegex();

    [GeneratedRegex(@"\bappName[""']?\s*:\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppNameRegex();
}
