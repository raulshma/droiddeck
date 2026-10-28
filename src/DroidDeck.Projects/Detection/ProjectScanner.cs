using System.Diagnostics;
using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Detection;

/// <summary>Scans a folder tree for project candidates with depth, folder, and time limits.</summary>
public sealed class ProjectScanner
{
    private readonly IReadOnlyList<IProjectDetector> _detectors;

    /// <summary>Creates a scanner with the supplied detectors.</summary>
    public ProjectScanner(IEnumerable<IProjectDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors.OrderBy(detector => detector.Priority).ToArray();
    }

    /// <summary>Scans for projects without adding them.</summary>
    public async Task<ProjectScanResult> ScanAsync(
        ProjectScanContext context,
        IReadOnlyCollection<string>? existingRoots = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var stopwatch = Stopwatch.StartNew();
        var timeout = context.Options.GetEffectiveTimeout();
        var maxDepth = context.Options.GetEffectiveDepth();
        var ignore = context.Options.IgnoreFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = (existingRoots ?? [])
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new ProjectScanResult { RootPath = context.RootPath };
        var candidates = new List<ProjectScanCandidate>();
        var pending = new Queue<(string Path, int Depth)>();
        if (context.Options.IncludeRoot)
        {
            pending.Enqueue((context.RootPath, 0));
        }
        else if (Directory.Exists(context.RootPath))
        {
            foreach (var child in EnumerateDirectories(context.RootPath, ignore))
            {
                pending.Enqueue((child, 1));
            }
        }

        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > timeout)
            {
                result.WasTruncated = true;
                result.TruncationReason = $"The scan stopped after {timeout.TotalSeconds:0.#} seconds.";
                break;
            }

            if (visited >= context.Options.MaxFolders)
            {
                result.WasTruncated = true;
                result.TruncationReason = $"The scan stopped after {context.Options.MaxFolders} folders.";
                break;
            }

            var (path, depth) = pending.Dequeue();
            visited++;
            var detectionContext = new ProjectScanContext(path, context.Options)
            {
                RepositoryRoot = context.RepositoryRoot ?? context.RootPath,
                Aapt2Path = context.Aapt2Path,
                JavaHome = context.JavaHome,
                GradlePath = context.GradlePath,
                AndroidSdkPath = context.AndroidSdkPath,
            };
            var detected = await DetectAsync(detectionContext, cancellationToken).ConfigureAwait(false);
            if (detected is not null)
            {
                candidates.Add(ToCandidate(detected.Project, known));
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in EnumerateDirectories(path, ignore))
            {
                pending.Enqueue((child, depth + 1));
            }
        }

        if (candidates.Count == 0 && context.Options.IncludeRoot)
        {
            candidates.Add(new ProjectScanCandidate
            {
                RootPath = context.RootPath,
                Name = new DirectoryInfo(context.RootPath).Name,
                Kind = ProjectKind.Custom,
                Confidence = DetectionConfidence.Low,
                AlreadyAdded = known.Contains(NormalizePath(context.RootPath)),
                RepositoryRoot = context.RepositoryRoot ?? context.RootPath,
            });
        }

        result.Candidates = candidates
            .OrderBy(candidate => candidate.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Duration = stopwatch.Elapsed;
        return result;
    }

    private async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken)
    {
        foreach (var detector in _detectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await detector.DetectAsync(context, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static ProjectScanCandidate ToCandidate(ProjectModel project, HashSet<string> known) => new()
    {
        RootPath = project.RootPath,
        Name = project.Name,
        Kind = project.Kind,
        Confidence = project.Confidence,
        ApplicationId = project.ApplicationId,
        VariantCount = project.Variants.Count,
        AlreadyAdded = known.Contains(NormalizePath(project.RootPath)),
        RepositoryRoot = project.RepositoryRoot,
    };

    private static IEnumerable<string> EnumerateDirectories(string path, HashSet<string> ignore)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return directories.Where(directory => !ignore.Contains(Path.GetFileName(directory)));
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
