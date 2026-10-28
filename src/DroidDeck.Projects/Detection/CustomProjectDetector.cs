using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Detection;

/// <summary>Creates the manual fallback project used when no stack detector matches.</summary>
public sealed class CustomProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "custom";

    /// <inheritdoc />
    public int Priority => 1_000;

    /// <inheritdoc />
    public Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var root = context.RootPath;
        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            Kind = ProjectKind.Custom,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.Custom),
            Confidence = DetectionConfidence.Low,
            Capabilities = ProjectCapability.Limited,
            Limitation = "No supported project marker was found. Define custom steps to build, install, and launch this project.",
            DetectedAt = DateTimeOffset.UtcNow,
            Warnings = ["No Gradle or APK markers were detected. Add custom steps or choose a different folder."],
            Evidence =
            [
                new DetectionEvidence
                {
                    Id = "custom-fallback",
                    Source = root,
                    Detail = "No known project marker matched, so the folder can be added as a custom project.",
                    Confidence = DetectionConfidence.Low,
                },
            ],
            RunConfigurations = [CreateDefaultConfiguration()],
        };
        return Task.FromResult<ProjectDetectionResult?>(new ProjectDetectionResult { Project = project });
    }

    /// <summary>Creates an empty custom run configuration.</summary>
    public static RunConfiguration CreateDefaultConfiguration() => new()
    {
        Id = "custom-default",
        Name = "Custom",
        Kind = RunConfigurationKind.Custom,
        IsDefault = true,
        Build = new BuildConfiguration { Skip = true },
        Artifact = new ArtifactConfiguration { IncludeSplits = true },
        Logs = new LogConfiguration(),
    };
}
