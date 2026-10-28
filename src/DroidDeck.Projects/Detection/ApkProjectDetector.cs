using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Detection;

/// <summary>Detects folders that contain prebuilt APK files.</summary>
public sealed class ApkProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "apk";

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(context.RootPath))
        {
            return Task.FromResult<ProjectDetectionResult?>(null);
        }

        var apks = Directory
            .EnumerateFiles(context.RootPath, "*.apk", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (apks.Length == 0)
        {
            return Task.FromResult<ProjectDetectionResult?>(null);
        }

        var root = context.RootPath;
        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(apks[0]),
            Name = Path.GetFileName(root),
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            Kind = ProjectKind.ApkOnly,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.ApkOnly),
            Confidence = DetectionConfidence.Medium,
            Capabilities = ProjectCapability.Install | ProjectCapability.Launch | ProjectCapability.Limited,
            Limitation = "APK-only projects install and launch prebuilt packages. Build, variants, and hot reload are unavailable.",
            DetectedAt = DateTimeOffset.UtcNow,
            ApkFiles = apks.Select(path => new ApkFileReference
            {
                Path = path,
                IsSplit = IsLikelySplit(path),
            }).ToList(),
            Evidence =
            [
                new DetectionEvidence
                {
                    Id = "apk-files",
                    Source = root,
                    Detail = $"{apks.Length} APK file{(apks.Length == 1 ? string.Empty : "s")} found in the folder.",
                    Confidence = DetectionConfidence.Medium,
                },
            ],
            Warnings = ["Inspect the APKs after adding to read the package name, version, SDK levels, and splits."],
        };
        project.RunConfigurations = CreateDefaultConfigurations(project);
        return Task.FromResult<ProjectDetectionResult?>(new ProjectDetectionResult { Project = project });
    }

    /// <summary>Returns whether an APK file name looks like a split or configuration APK.</summary>
    public static bool IsLikelySplit(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("split_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("config.", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("-split", StringComparison.OrdinalIgnoreCase);
    }

    internal static List<RunConfiguration> CreateDefaultConfigurations(ProjectModel project)
    {
        var baseApk = project.ApkFiles.FirstOrDefault(reference => !reference.IsSplit) ?? project.ApkFiles.FirstOrDefault();
        return
        [
            new RunConfiguration
            {
                Id = "apk-default",
                Name = "Install APK",
                Kind = RunConfigurationKind.Apk,
                IsDefault = true,
                Build = new BuildConfiguration { Skip = true },
                Artifact = new ArtifactConfiguration
                {
                    Glob = baseApk?.Path ?? string.Empty,
                    IncludeSplits = true,
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
