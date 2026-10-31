using System.Text.RegularExpressions;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects Unity projects from their Assets and ProjectSettings folders, reads the editor version
/// and player settings, and finds APKs Unity has already built. The detector never invokes Unity.
/// </summary>
public sealed partial class UnityProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "unity";

    /// <inheritdoc />
    public int Priority => 13;

    /// <inheritdoc />
    public async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = context.RootPath;
        var assets = Path.Combine(root, "Assets");
        var settings = Path.Combine(root, "ProjectSettings");
        if (!Directory.Exists(assets) || !Directory.Exists(settings))
        {
            return null;
        }

        var versionText = await ProjectSource.ReadTextAsync(
            Path.Combine(settings, "ProjectVersion.txt"),
            cancellationToken).ConfigureAwait(false);
        var playerSettingsText = await ProjectSource.ReadTextAsync(
            Path.Combine(settings, "ProjectSettings.asset"),
            cancellationToken).ConfigureAwait(false);
        var editorVersion = ProjectSource.MatchValue(EditorVersionRegex(), versionText)
            ?? ProjectSource.MatchValue(EditorVersionWithRevisionRegex(), versionText);
        var player = ParsePlayerSettings(playerSettingsText);
        var (artifactGlob, apkFiles) = FindBuiltApks(root);

        var evidence = new List<DetectionEvidence>
        {
            new()
            {
                Id = "unity-project",
                Source = "ProjectSettings/ProjectVersion.txt",
                Detail = "The Assets and ProjectSettings folders mark a Unity project.",
                Confidence = DetectionConfidence.High,
            },
        };
        if (editorVersion is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "unity-editor-version",
                Source = "ProjectSettings/ProjectVersion.txt",
                Detail = $"The project records Unity editor {editorVersion}.",
                Snippet = editorVersion,
                Confidence = DetectionConfidence.High,
            });
        }

        if (player.ProductName is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "unity-product-name",
                Source = "ProjectSettings/ProjectSettings.asset",
                Detail = $"The player settings name the product {player.ProductName}.",
                Snippet = player.ProductName,
                Confidence = DetectionConfidence.Medium,
            });
        }

        if (player.ApplicationId is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "unity-application-id",
                Source = "ProjectSettings/ProjectSettings.asset",
                Detail = $"The Android bundle identifier is {player.ApplicationId}.",
                Snippet = player.ApplicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        if (apkFiles.Count > 0)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "unity-apk",
                Source = Path.GetRelativePath(root, apkFiles[0]).Replace('\\', '/'),
                Detail = apkFiles.Count == 1
                    ? "A built APK was found under the Builds folder."
                    : $"{apkFiles.Count} built APKs were found under the Builds folder.",
                Confidence = DetectionConfidence.High,
            });
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = player.ProductName ?? new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
            Kind = ProjectKind.Unity,
            KindLabel = "Unity (custom support)",
            Confidence = editorVersion is not null && player.ApplicationId is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = player.ApplicationId,
            ApplicationLabel = player.ProductName,
            UnityInfo = new UnityProjectInfo
            {
                EditorVersion = editorVersion,
                ProductName = player.ProductName,
                CompanyName = player.CompanyName,
            },
            Capabilities = ProjectCapability.Install |
                           ProjectCapability.Launch |
                           ProjectCapability.MultiDevice |
                           ProjectCapability.Limited,
            Evidence = evidence,
            Limitation =
                "Unity builds are not automated: DroidDeck never starts a generic one-click Unity build. " +
                "Open the project in Unity, switch the platform to Android, and build the APK (or run the " +
                $"optional {UnityAdapter.BuildMethod} custom build method), then use the \"Install built APK\" " +
                "configuration. Write the Editor build template with \"Write build script\"; it is written to " +
                $"{UnityAdapter.BuildTemplatePath} and runs in batch mode as: Unity.exe -quit -batchmode " +
                "-projectPath \"<project>\" -executeMethod " + UnityAdapter.BuildMethod + " -logFile -",
        };

        project.RunConfigurations = UnityAdapter.CreateDefaultRunConfigurations(project);
        if (artifactGlob is not null)
        {
            project.RunConfigurations[0].Artifact.Glob = artifactGlob;
        }

        if (apkFiles.Count == 0)
        {
            project.Warnings.Add(
                "No built APK was found under the Builds folder. Build the project in Unity or set the artifact glob in the run configuration.");
        }

        if (editorVersion is null)
        {
            project.Warnings.Add(
                "The Unity editor version could not be read from ProjectSettings/ProjectVersion.txt.");
        }

        if (player.ApplicationId is null)
        {
            project.Warnings.Add(
                "The Android bundle identifier was not found in ProjectSettings.asset. Set the launch package name in the run configuration.");
        }

        return new ProjectDetectionResult { Project = project };
    }

    private static (string? ArtifactGlob, List<string> Files) FindBuiltApks(string root)
    {
        var builds = Path.Combine(root, UnityAdapter.BuildFolder);
        if (!Directory.Exists(builds))
        {
            return (null, []);
        }

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(builds, "*.apk", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, []);
        }

        if (files.Count == 0)
        {
            return (null, files);
        }

        var directories = files
            .Select(file => Path.GetDirectoryName(file)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (directories.Count != 1)
        {
            return (null, files);
        }

        var relative = Path.GetRelativePath(root, directories[0]).Replace('\\', '/');
        return (UnityAdapter.GetArtifactGlob(relative), files);
    }

    private static PlayerSettings ParsePlayerSettings(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new PlayerSettings(null, null, null);
        }

        return new PlayerSettings(
            ProjectSource.MatchValue(ProductNameRegex(), text),
            ProjectSource.MatchValue(CompanyNameRegex(), text),
            ParseAndroidIdentifier(text));
    }

    private static string? ParseAndroidIdentifier(string text)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            var marker = line.IndexOf("applicationIdentifier:", StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var inline = line[(marker + "applicationIdentifier:".Length)..].Trim();
            if (inline.Length > 0)
            {
                return inline;
            }

            var indent = line.Length - line.TrimStart().Length;
            for (var nested = index + 1; nested < lines.Length; nested++)
            {
                var candidate = lines[nested].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (candidate.Length - candidate.TrimStart().Length <= indent)
                {
                    break;
                }

                var trimmed = candidate.Trim();
                if (trimmed.StartsWith("Android:", StringComparison.Ordinal))
                {
                    var value = trimmed["Android:".Length..].Trim();
                    return value.Length == 0 ? null : value;
                }
            }
        }

        return null;
    }

    private sealed record PlayerSettings(string? ProductName, string? CompanyName, string? ApplicationId);

    [GeneratedRegex(@"^m_EditorVersion:\s*(?<value>\S+)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EditorVersionRegex();

    [GeneratedRegex(@"^m_EditorVersionWithRevision:\s*(?<value>\S+)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EditorVersionWithRevisionRegex();

    [GeneratedRegex(@"^\s*productName:\s*(?<value>.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProductNameRegex();

    [GeneratedRegex(@"^\s*companyName:\s*(?<value>.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CompanyNameRegex();
}
