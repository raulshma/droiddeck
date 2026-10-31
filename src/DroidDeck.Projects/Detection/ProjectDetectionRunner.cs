using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Owns the detection policy shared by scanning and project adds: detectors run in priority
/// order, the first non-null result wins, an explicit kind relabels a detected project, and a
/// fallback detector or a minimal default project covers folders that no detector recognizes.
/// </summary>
internal sealed class ProjectDetectionRunner
{
    private readonly IReadOnlyList<IProjectDetector> _detectors;
    private readonly IProjectDetector? _fallbackDetector;

    /// <summary>Creates a runner that orders the supplied detectors by priority.</summary>
    /// <param name="detectors">The detectors to run.</param>
    /// <param name="fallbackDetector">The detector used when no detector matches, or <see langword="null"/>.</param>
    internal ProjectDetectionRunner(
        IEnumerable<IProjectDetector> detectors,
        IProjectDetector? fallbackDetector = null)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors.OrderBy(detector => detector.Priority).ToArray();
        _fallbackDetector = fallbackDetector;
    }

    /// <summary>Runs detectors in priority order and returns the first non-null result.</summary>
    /// <param name="context">The folder being inspected.</param>
    /// <param name="cancellationToken">A token that cancels detection.</param>
    /// <returns>The first match, or <see langword="null"/> when no detector applies.</returns>
    internal async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
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

    /// <summary>
    /// Detects one project for the supplied kind. A custom kind runs only the fallback detector;
    /// other kinds relabel the first detected project, and the fallback detector or a minimal
    /// default project covers folders that no detector recognizes.
    /// </summary>
    /// <param name="context">The folder being inspected.</param>
    /// <param name="kind">The explicitly requested kind, or <see langword="null"/> to use the detected kind.</param>
    /// <param name="cancellationToken">A token that cancels detection.</param>
    /// <returns>The detected project, the fallback project, or a minimal default project.</returns>
    internal async Task<ProjectModel> DetectProjectAsync(
        ProjectScanContext context,
        ProjectKind? kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (kind == ProjectKind.Custom)
        {
            var custom = await RunFallbackAsync(context, cancellationToken).ConfigureAwait(false);
            return custom?.Project ?? CreateDefaultProject(context.RootPath, kind: null, includeId: false);
        }

        var result = await DetectAsync(context, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            if (kind is not null && result.Project.Kind != kind)
            {
                result.Project.Kind = kind.Value;
                result.Project.KindLabel = ProjectModel.GetKindLabel(kind.Value);
            }

            return result.Project;
        }

        var fallback = await RunFallbackAsync(context, cancellationToken).ConfigureAwait(false);
        return fallback?.Project ?? CreateDefaultProject(context.RootPath, kind, includeId: true);
    }

    private async Task<ProjectDetectionResult?> RunFallbackAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken)
    {
        if (_fallbackDetector is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await _fallbackDetector.DetectAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static ProjectModel CreateDefaultProject(string rootPath, ProjectKind? kind, bool includeId)
    {
        var resolvedKind = kind ?? ProjectKind.Custom;
        return new ProjectModel
        {
            Id = includeId ? ProjectModel.CreateId(rootPath) : string.Empty,
            Name = new DirectoryInfo(rootPath).Name,
            RootPath = rootPath,
            Kind = resolvedKind,
            KindLabel = ProjectModel.GetKindLabel(resolvedKind),
        };
    }
}
