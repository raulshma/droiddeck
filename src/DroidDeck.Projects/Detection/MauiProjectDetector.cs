using System.Xml.Linq;
using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects .NET MAUI projects from their project file. The detector reads the target frameworks,
/// application ID, and title without restoring or building the project.
/// </summary>
public sealed class MauiProjectDetector : IProjectDetector
{
    private static readonly string[] IgnoredFolders =
    [
        ".git", ".gradle", ".idea", ".vs", "bin", "obj", "node_modules", "packages", "platforms",
    ];

    /// <inheritdoc />
    public string Id => "maui";

    /// <inheritdoc />
    public int Priority => 14;

    /// <inheritdoc />
    public async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = context.RootPath;
        foreach (var projectFile in FindProjectFiles(root))
        {
            var text = await ReadTextAsync(projectFile, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            var info = TryParseProject(text);
            if (info is null)
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(root, projectFile).Replace('\\', '/');
            return new ProjectDetectionResult
            {
                Project = CreateProject(root, relativePath, info),
            };
        }

        return null;
    }

    private static ProjectModel CreateProject(string root, string relativePath, ParsedProject parsed)
    {
        var info = parsed.Info;
        info.ProjectFile = relativePath;
        var name = string.IsNullOrWhiteSpace(parsed.ApplicationTitle)
            ? Path.GetFileNameWithoutExtension(relativePath)
            : parsed.ApplicationTitle;
        var evidence = new List<DetectionEvidence>
        {
            new()
            {
                Id = "maui-project",
                Source = relativePath,
                Detail = parsed.UsesUseMaui
                    ? "The project file enables the MAUI workload with UseMaui."
                    : "The project file references Microsoft.Maui.Controls.",
                Confidence = parsed.UsesUseMaui ? DetectionConfidence.High : DetectionConfidence.Medium,
            },
        };
        if (info.SupportsAndroid)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "maui-android-target",
                Source = relativePath,
                Detail = $"The project targets Android with {string.Join(", ", info.AndroidTargetFrameworks)}.",
                Snippet = string.Join(";", info.AndroidTargetFrameworks),
                Confidence = DetectionConfidence.High,
            });
        }

        if (!string.IsNullOrWhiteSpace(parsed.ApplicationId))
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "maui-application-id",
                Source = relativePath,
                Detail = $"The project declares application ID {parsed.ApplicationId}.",
                Snippet = parsed.ApplicationId,
                Confidence = DetectionConfidence.High,
            });
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = name,
            RootPath = root,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
            Kind = ProjectKind.Maui,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.Maui),
            Confidence = parsed.UsesUseMaui && info.SupportsAndroid && parsed.ApplicationId is not null
                ? DetectionConfidence.High
                : DetectionConfidence.Medium,
            ApplicationId = parsed.ApplicationId,
            ApplicationLabel = parsed.ApplicationTitle,
            MauiInfo = info,
            Evidence = evidence,
        };

        if (!info.SupportsAndroid)
        {
            project.Capabilities = ProjectCapability.Limited;
            project.Limitation =
                $"This MAUI project does not target Android, so DroidDeck cannot build or deploy it. Add an " +
                $"Android framework such as {MauiAdapter.DefaultTargetFramework} to TargetFrameworks in " +
                $"{relativePath}, then refresh the project. The .NET workload must also be installed; the " +
                "diagnostics page reports its state.";
            return project;
        }

        project.Capabilities = ProjectCapability.Build |
                               ProjectCapability.Install |
                               ProjectCapability.Launch |
                               ProjectCapability.Variants |
                               ProjectCapability.MultiDevice;
        project.Variants = MauiAdapter.CreateVariants(info);
        project.RunConfigurations = MauiAdapter.CreateDefaultRunConfigurations(project, info);
        if (string.IsNullOrWhiteSpace(parsed.ApplicationId))
        {
            project.Warnings.Add(
                "The project file does not declare an ApplicationId. Set the launch package name in the run configuration.");
        }

        return project;
    }

    private static List<string> FindProjectFiles(string root)
    {
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly));
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (IgnoredFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.AddRange(Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return candidates;
    }

    private static ParsedProject? TryParseProject(string text)
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

        var elements = document.Descendants().ToList();
        var usesUseMaui = elements.Any(element =>
            element.Name.LocalName == "UseMaui" &&
            string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        var referencesControls = elements.Any(element =>
            element.Name.LocalName == "PackageReference" &&
            (element.Attribute("Include")?.Value ?? string.Empty)
                .StartsWith("Microsoft.Maui.Controls", StringComparison.OrdinalIgnoreCase));
        if (!usesUseMaui && !referencesControls)
        {
            return null;
        }

        var frameworks = new List<string>();
        foreach (var element in elements.Where(element =>
                     element.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
        {
            foreach (var token in element.Value.Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith("net", StringComparison.OrdinalIgnoreCase) ||
                    token.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!frameworks.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    frameworks.Add(token);
                }
            }
        }

        var info = new MauiProjectInfo
        {
            TargetFrameworks = frameworks,
            AndroidTargetFrameworks = [.. frameworks.Where(MauiAdapter.IsAndroidTargetFramework)],
            ReferencesMauiControls = referencesControls,
        };
        return new ParsedProject(
            info,
            usesUseMaui,
            CleanValue(elements, "ApplicationId"),
            CleanValue(elements, "ApplicationTitle"));
    }

    private static string? CleanValue(IEnumerable<XElement> elements, string name)
    {
        var value = elements.FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) || value.Contains("$(", StringComparison.Ordinal)
            ? null
            : value;
    }

    private static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ParsedProject(
        MauiProjectInfo Info,
        bool UsesUseMaui,
        string? ApplicationId,
        string? ApplicationTitle);
}
