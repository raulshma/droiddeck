using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Adapters;

/// <summary>Configures a <see cref="WebHybridAdapter"/> for one web-hybrid toolchain.</summary>
internal sealed record WebHybridProfile(
    ProjectKind Kind,
    RunConfigurationKind RunConfigurationKind,
    string IdPrefix,
    string AndroidHostFolder,
    string ArtifactFolder,
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> PrepareArguments);

/// <summary>
/// Builds variants, run configurations, artifact globs, and CLI preparation arguments for a
/// JavaScript web-hybrid toolchain that wraps a generated Android host.
/// </summary>
internal sealed class WebHybridAdapter
{
    private const int BuildTimeoutSeconds = 1_200;
    private readonly WebHybridProfile _profile;

    internal WebHybridAdapter(WebHybridProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    internal WebHybridProfile Profile => _profile;

    internal IReadOnlyList<string> Modes => _profile.Modes;

    internal static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "debug";
        }

        var normalized = mode.Trim().ToLowerInvariant();
        return normalized is "release" ? normalized : "debug";
    }

    internal string GetArtifactGlob(string? mode) =>
        $"{_profile.ArtifactFolder}/{NormalizeMode(mode)}/*.apk";

    internal static string GetBuildTask(string? mode)
    {
        var normalized = NormalizeMode(mode);
        return "assemble" + (char.ToUpperInvariant(normalized[0]) + normalized[1..]);
    }

    internal IReadOnlyList<string> CreatePrepareArguments() => _profile.PrepareArguments;

    internal List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var configurations = new List<RunConfiguration>();
        foreach (var mode in Modes)
        {
            configurations.Add(new RunConfiguration
            {
                Id = $"{_profile.IdPrefix}-{mode}",
                Name = char.ToUpperInvariant(mode[0]) + mode[1..],
                Kind = _profile.RunConfigurationKind,
                VariantName = mode,
                IsDefault = mode == "debug",
                Build = new BuildConfiguration
                {
                    Task = GetBuildTask(mode),
                    WorkingDirectory = _profile.AndroidHostFolder,
                    TimeoutSeconds = BuildTimeoutSeconds,
                },
                Artifact = new ArtifactConfiguration
                {
                    Glob = GetArtifactGlob(mode),
                    PackageName = project.ApplicationId,
                },
                Launch = new LaunchConfiguration
                {
                    PackageName = project.ApplicationId,
                    Activity = project.LauncherActivity,
                },
            });
        }

        return configurations;
    }

    internal List<ProjectVariant> CreateVariants()
    {
        var variants = new List<ProjectVariant>();
        foreach (var mode in Modes)
        {
            variants.Add(new ProjectVariant
            {
                Name = mode,
                BuildType = mode,
                ArtifactGlob = GetArtifactGlob(mode),
                Source = DetectionSource.Detected,
                IsDefault = mode == "debug",
            });
        }

        return variants;
    }
}
