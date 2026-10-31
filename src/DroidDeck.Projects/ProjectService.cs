using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;
using DroidDeck.Projects.Detection;
using DroidDeck.Projects.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Projects;

/// <summary>Loads, detects, persists, and refreshes projects.</summary>
public sealed class ProjectService : IProjectService, IDisposable
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IProjectStore _store;
    private readonly ProjectDetectionRunner _detectionRunner;
    private readonly ProjectScanner _scanner;
    private readonly IApkInspector _apkInspector;
    private readonly IConfigurationService? _configuration;
    private readonly IToolchainService? _toolchain;
    private readonly ILogger<ProjectService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ProjectModel> _projects = [];
    private bool _initialized;

    /// <summary>Creates the project service.</summary>
    public ProjectService(
        IProjectStore store,
        IEnumerable<IProjectDetector> detectors,
        IApkInspector apkInspector,
        IConfigurationService? configuration = null,
        IToolchainService? toolchain = null,
        ILogger<ProjectService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(detectors);
        _detectionRunner = new ProjectDetectionRunner(detectors, new CustomProjectDetector());
        _scanner = new ProjectScanner(_detectionRunner);
        _apkInspector = apkInspector ?? throw new ArgumentNullException(nameof(apkInspector));
        _configuration = configuration;
        _toolchain = toolchain;
        _logger = logger ?? NullLogger<ProjectService>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProjectModel> Projects
    {
        get
        {
            lock (_projects)
            {
                return _projects
                    .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool IsInitialized => _initialized;

    /// <inheritdoc />
    public string? Aapt2Path => ResolveAapt2Path();

    /// <inheritdoc />
    public event EventHandler<ProjectModel>? ProjectChanged;

    /// <inheritdoc />
    public event EventHandler? ProjectsChanged;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var loaded = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            lock (_projects)
            {
                _projects.Clear();
                _projects.AddRange(loaded);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task<ProjectScanResult> ScanAsync(
        string rootPath,
        ProjectScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalized = NormalizeDirectory(rootPath);
        var effectiveOptions = options ?? CreateScanOptions();
        var context = CreateContext(normalized, effectiveOptions);
        var existing = Projects.Select(project => project.RootPath).ToArray();
        return await _scanner.ScanAsync(context, existing, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectModel> AddAsync(
        string rootPath,
        ProjectKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = NormalizeDirectory(rootPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = FindByRoot(root);
            if (existing is not null)
            {
                existing.LastOpenedAt = DateTimeOffset.UtcNow;
                await _store.SaveAsync(existing, cancellationToken).ConfigureAwait(false);
                return existing;
            }

            var detected = await DetectAsync(root, kind, cancellationToken).ConfigureAwait(false);
            detected.Id = ProjectModel.CreateId(detected.RootPath);
            detected.Name = string.IsNullOrWhiteSpace(detected.Name)
                ? new DirectoryInfo(root).Name
                : detected.Name;
            detected.CreatedAt = DateTimeOffset.UtcNow;
            detected.DetectedAt = DateTimeOffset.UtcNow;
            detected.LastOpenedAt = DateTimeOffset.UtcNow;
            ApplyManualOverrides(detected);
            await _store.SaveAsync(detected, cancellationToken).ConfigureAwait(false);
            AddToCache(detected);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Added project {Project} ({Kind}) at {Root}.", detected.Name, detected.Kind, root);
            }
        }
        finally
        {
            _gate.Release();
        }

        var project = FindByRoot(root) ?? throw new InvalidOperationException("The project could not be added.");
        ProjectsChanged?.Invoke(this, EventArgs.Empty);
        ProjectChanged?.Invoke(this, project);
        return project;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectModel>> AddAllAsync(
        string rootPath,
        ProjectScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scan = await ScanAsync(rootPath, options, cancellationToken).ConfigureAwait(false);
        var added = new List<ProjectModel>();
        foreach (var candidate in scan.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Kind == ProjectKind.ApkOnly)
            {
                var apks = Directory.Exists(candidate.RootPath)
                    ? Directory.GetFiles(candidate.RootPath, "*.apk", SearchOption.TopDirectoryOnly)
                    : [];
                if (apks.Length == 0)
                {
                    continue;
                }

                added.Add(await AddApkAsync(apks, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                added.Add(await AddAsync(candidate.RootPath, candidate.Kind, cancellationToken).ConfigureAwait(false));
            }
        }

        return added;
    }

    /// <inheritdoc />
    public async Task<ProjectModel> AddApkAsync(
        IReadOnlyList<string> apkPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apkPaths);
        var files = apkPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(File.Exists)
            .Distinct(PathComparer)
            .ToArray();
        if (files.Length == 0)
        {
            throw new FileNotFoundException("No APK files were supplied or the files do not exist.");
        }

        if (files.Any(path => !path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Every supplied file must have the .apk extension.");
        }

        var inspections = await _apkInspector.InspectManyAsync(files, cancellationToken).ConfigureAwait(false);
        var valid = new List<(string Path, ApkInspectionResult Result)>();
        for (var index = 0; index < files.Length; index++)
        {
            if (index < inspections.Count && inspections[index].Succeeded)
            {
                valid.Add((files[index], inspections[index]));
            }
        }

        if (valid.Count == 0)
        {
            var error = inspections.FirstOrDefault(result => result.Error is not null)?.Error
                ?? "aapt2 could not read the APK.";
            var guidance = inspections.FirstOrDefault(result => result.Guidance is not null)?.Guidance
                ?? "Check that the file is a valid APK and that aapt2 is available in the Android SDK build-tools.";
            throw new InvalidDataException($"The APK could not be inspected: {error} {guidance}");
        }

        var baseEntry = valid.FirstOrDefault(entry => !entry.Result.Info!.IsSplit);
        if (baseEntry.Path is null)
        {
            baseEntry = valid[0];
        }

        var baseApk = baseEntry.Path;
        var info = baseEntry.Result.Info!;
        var root = Path.GetDirectoryName(baseApk) ?? throw new InvalidOperationException("The APK has no parent folder.");
        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(baseApk),
            Name = string.IsNullOrWhiteSpace(info.ApplicationLabel) ? info.PackageName : info.ApplicationLabel,
            RootPath = root,
            Kind = ProjectKind.ApkOnly,
            KindLabel = ProjectModel.GetKindLabel(ProjectKind.ApkOnly),
            Confidence = DetectionConfidence.High,
            ApplicationId = info.PackageName,
            LauncherActivity = info.LaunchableActivity,
            ApplicationLabel = info.ApplicationLabel,
            Capabilities = ProjectCapability.Install | ProjectCapability.Launch | ProjectCapability.Limited,
            Limitation = "APK-only projects install and launch prebuilt packages. Build, variants, and hot reload are unavailable.",
            CreatedAt = DateTimeOffset.UtcNow,
            DetectedAt = DateTimeOffset.UtcNow,
            LastOpenedAt = DateTimeOffset.UtcNow,
            ApkFiles = valid
                .OrderByDescending(entry => string.Equals(entry.Path, baseApk, StringComparison.OrdinalIgnoreCase))
                .Select(entry => new ApkFileReference
                {
                    Path = entry.Path,
                    IsSplit = entry.Result.Info?.IsSplit == true,
                    SplitName = entry.Result.Info?.SplitName,
                })
                .ToList(),
            Evidence =
            [
                new DetectionEvidence
                {
                    Id = "apk-inspection",
                    Source = Path.GetFileName(baseApk),
                    Detail = $"aapt2 reported package {info.PackageName}" +
                             (string.IsNullOrEmpty(info.VersionName) ? string.Empty : $" version {info.VersionName}") +
                             ".",
                    Confidence = DetectionConfidence.High,
                },
            ],
        };
        project.RunConfigurations = ApkProjectDetector.CreateDefaultConfigurations(project);
        foreach (var configuration in project.RunConfigurations)
        {
            configuration.Artifact.Glob = baseApk;
            configuration.Artifact.PackageName = info.PackageName;
            configuration.Launch.PackageName = info.PackageName;
            configuration.Launch.Activity = info.LaunchableActivity;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = FindByRoot(root);
            if (existing is not null && existing.Kind == ProjectKind.ApkOnly &&
                string.Equals(existing.ApplicationId, info.PackageName, StringComparison.Ordinal))
            {
                return existing;
            }

            await _store.SaveAsync(project, cancellationToken).ConfigureAwait(false);
            AddToCache(project);
        }
        finally
        {
            _gate.Release();
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
        ProjectChanged?.Invoke(this, project);
        return project;
    }

    /// <inheritdoc />
    public ProjectModel? Get(string projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return null;
        }

        lock (_projects)
        {
            return _projects.FirstOrDefault(project =>
                string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ProjectModel project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(project, cancellationToken).ConfigureAwait(false);
            ReplaceInCache(project);
        }
        finally
        {
            _gate.Release();
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
        ProjectChanged?.Invoke(this, project);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteAsync(projectId, cancellationToken).ConfigureAwait(false);
            lock (_projects)
            {
                _projects.RemoveAll(project =>
                    string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            _gate.Release();
        }

        ProjectsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task MarkOpenedAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var project = Get(projectId);
        if (project is null)
        {
            return;
        }

        project.LastOpenedAt = DateTimeOffset.UtcNow;
        await SaveAsync(project, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectRefreshDiff> PreviewRefreshAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var stored = Get(projectId) ?? throw new InvalidOperationException($"Project {projectId} was not found.");
        var detected = await DetectExistingAsync(stored, cancellationToken).ConfigureAwait(false);
        detected.Id = stored.Id;
        detected.Name = stored.Name;
        detected.RootPath = stored.RootPath;
        detected.CreatedAt = stored.CreatedAt;
        detected.RepositoryRoot = stored.RepositoryRoot;
        detected.ManualOverrides = new Dictionary<string, string>(stored.ManualOverrides, StringComparer.OrdinalIgnoreCase);
        detected.RunConfigurations = stored.RunConfigurations;
        ApplyManualOverrides(detected);
        var changes = ProjectRefreshFields.Compare(stored, detected);
        return new ProjectRefreshDiff
        {
            ProjectId = stored.Id,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectedProject = detected,
            Changes = changes,
        };
    }

    /// <inheritdoc />
    public async Task<ProjectModel> ApplyRefreshAsync(
        ProjectRefreshDiff diff,
        IReadOnlyCollection<string> acceptedFields,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(acceptedFields);
        var project = Get(diff.ProjectId) ?? throw new InvalidOperationException($"Project {diff.ProjectId} was not found.");
        var detected = diff.DetectedProject;
        ProjectRefreshFields.Apply(project, detected, acceptedFields);

        project.DetectedAt = diff.DetectedAt;
        project.Confidence = detected.Confidence;
        project.Evidence = detected.Evidence;
        project.Warnings = detected.Warnings;
        await SaveAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    /// <inheritdoc />
    public async Task<ProjectModel> RefreshAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var diff = await PreviewRefreshAsync(projectId, cancellationToken).ConfigureAwait(false);
        var fields = diff.Changes.Select(change => change.Field).ToArray();
        return await ApplyRefreshAsync(diff, fields, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectModel> DetectExistingAsync(ProjectModel project, CancellationToken cancellationToken)
    {
        if (project.Kind == ProjectKind.ApkOnly)
        {
            var apks = project.ApkFiles
                .Select(reference => reference.Path)
                .Where(File.Exists)
                .ToArray();
            if (apks.Length == 0 && Directory.Exists(project.RootPath))
            {
                apks = Directory.GetFiles(project.RootPath, "*.apk", SearchOption.TopDirectoryOnly);
            }

            if (apks.Length > 0)
            {
                var clone = project.Clone();
                var inspections = await _apkInspector.InspectManyAsync(apks, cancellationToken).ConfigureAwait(false);
                var info = inspections.FirstOrDefault(result => result.Succeeded)?.Info;
                if (info is not null)
                {
                    clone.ApplicationId = info.PackageName;
                    clone.LauncherActivity = info.LaunchableActivity;
                    clone.ApplicationLabel = info.ApplicationLabel;
                    clone.ApkFiles = apks.Select(path => new ApkFileReference
                    {
                        Path = path,
                        IsSplit = ApkProjectDetector.IsLikelySplit(path),
                    }).ToList();
                    clone.Evidence =
                    [
                        new DetectionEvidence
                        {
                            Id = "apk-inspection",
                            Source = Path.GetFileName(apks[0]),
                            Detail = $"aapt2 reported package {info.PackageName}.",
                            Confidence = DetectionConfidence.High,
                        },
                    ];
                    return clone;
                }
            }

            return project.Clone();
        }

        if (project.Kind == ProjectKind.Custom)
        {
            return project.Clone();
        }

        var context = CreateContext(project.RootPath, CreateScanOptions());
        var detected = await _detectionRunner.DetectAsync(context, cancellationToken).ConfigureAwait(false);
        return detected?.Project ?? project.Clone();
    }

    private Task<ProjectModel> DetectAsync(
        string root,
        ProjectKind? kind,
        CancellationToken cancellationToken)
    {
        var context = CreateContext(root, CreateScanOptions());
        return _detectionRunner.DetectProjectAsync(context, kind, cancellationToken);
    }

    private ProjectScanOptions CreateScanOptions()
    {
        var settings = _configuration?.Current.Settings.Projects;
        if (settings is null)
        {
            return new ProjectScanOptions();
        }

        return new ProjectScanOptions
        {
            MaxDepth = settings.ScanMonorepos ? settings.ScanDepth : 0,
            MaxFolders = Math.Max(1, settings.MaxScanFolders),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.ScanTimeoutSeconds)),
            IgnoreFolders = settings.IgnoreFolders.Count > 0
                ? [.. settings.IgnoreFolders]
                : ProjectScanOptions.Default.IgnoreFolders,
            IncludeRoot = true,
            DetectNestedGradleProjects = true,
        };
    }

    private ProjectScanContext CreateContext(string root, ProjectScanOptions options)
    {
        var snapshot = _toolchain?.CachedSnapshot;
        var settings = _configuration?.Current.Settings.Tools;
        return new ProjectScanContext(root, options)
        {
            RepositoryRoot = root,
            Aapt2Path = ResolveAapt2Path(),
            JavaHome = settings?.JavaHome ?? snapshot?.Get(ToolKind.Java)?.Path,
            GradlePath = settings?.GradlePath ?? snapshot?.Get(ToolKind.Gradle)?.Path,
            AndroidSdkPath = settings?.AndroidSdkPath ?? snapshot?.AndroidSdkPath,
        };
    }

    private string? ResolveAapt2Path() => ToolPathLocator.ResolveAapt2(_configuration, _toolchain);

    private static void ApplyManualOverrides(ProjectModel project)
    {
        foreach (var (field, value) in project.ManualOverrides)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            switch (field.ToLowerInvariant())
            {
                case "name":
                    project.Name = value;
                    break;
                case "applicationid":
                    project.ApplicationId = value;
                    break;
                case "namespace":
                    project.Namespace = value;
                    break;
                case "launcheractivity":
                    project.LauncherActivity = value;
                    break;
                case "applicationmodule":
                    project.ApplicationModule = value;
                    break;
                case "kind":
                    if (Enum.TryParse<ProjectKind>(value, ignoreCase: true, out var kind))
                    {
                        project.Kind = kind;
                        project.KindLabel = ProjectModel.GetKindLabel(kind);
                    }

                    break;
            }
        }
    }

    private ProjectModel? FindByRoot(string root)
    {
        var normalized = NormalizeDirectory(root);
        lock (_projects)
        {
            return _projects.FirstOrDefault(project =>
                PathComparer.Equals(NormalizeDirectory(project.RootPath), normalized));
        }
    }

    private void AddToCache(ProjectModel project)
    {
        lock (_projects)
        {
            _projects.RemoveAll(existing =>
                string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase));
            _projects.Add(project);
        }
    }

    private void ReplaceInCache(ProjectModel project)
    {
        lock (_projects)
        {
            var index = _projects.FindIndex(existing =>
                string.Equals(existing.Id, project.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _projects[index] = project;
            }
            else
            {
                _projects.Add(project);
            }
        }
    }

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
