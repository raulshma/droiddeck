namespace DroidDeck.Core.Projects;

/// <summary>Context supplied to a project detector.</summary>
public sealed class ProjectScanContext
{
    /// <summary>Creates a scan context.</summary>
    public ProjectScanContext(string rootPath, ProjectScanOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Gets the absolute folder being inspected.</summary>
    public string RootPath { get; }

    /// <summary>Gets the scan limits.</summary>
    public ProjectScanOptions Options { get; }

    /// <summary>Gets or sets the repository root that contains the project.</summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>Gets or sets the aapt2 path when one is available.</summary>
    public string? Aapt2Path { get; set; }

    /// <summary>Gets or sets the JDK path when one is available.</summary>
    public string? JavaHome { get; set; }

    /// <summary>Gets or sets the Gradle path when one is available.</summary>
    public string? GradlePath { get; set; }

    /// <summary>Gets or sets the Android SDK path when one is available.</summary>
    public string? AndroidSdkPath { get; set; }

    /// <summary>Builds a path relative to the project root.</summary>
    public string Combine(params string[] segments) => Path.Combine([RootPath, .. segments]);
}

/// <summary>The result of running one detector.</summary>
public sealed class ProjectDetectionResult
{
    /// <summary>Gets or sets the detected project.</summary>
    public ProjectModel Project { get; set; } = new();

    /// <summary>Gets or sets extra candidates found inside the same folder.</summary>
    public List<ProjectScanCandidate> AdditionalCandidates { get; set; } = [];
}

/// <summary>Detects a project type in a folder without invoking build tools.</summary>
public interface IProjectDetector
{
    /// <summary>Gets the stable detector identifier.</summary>
    string Id { get; }

    /// <summary>Gets the detector priority; lower values run first.</summary>
    int Priority { get; }

    /// <summary>Detects a project, returning <see langword="null"/> when the detector does not apply.</summary>
    Task<ProjectDetectionResult?> DetectAsync(ProjectScanContext context, CancellationToken cancellationToken = default);
}

/// <summary>Persists project documents.</summary>
public interface IProjectStore
{
    /// <summary>Lists all stored projects.</summary>
    Task<IReadOnlyList<ProjectModel>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads one project, or <see langword="null"/> when it does not exist.</summary>
    Task<ProjectModel?> GetAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Writes a project atomically.</summary>
    Task SaveAsync(ProjectModel project, CancellationToken cancellationToken = default);

    /// <summary>Deletes a project.</summary>
    Task DeleteAsync(string projectId, CancellationToken cancellationToken = default);
}

/// <summary>Describes one detected change during a refresh.</summary>
public sealed class ProjectFieldChange
{
    /// <summary>Gets or sets the field path, such as <c>modules</c> or <c>applicationId</c>.</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Gets or sets the current value.</summary>
    public string? OldValue { get; set; }

    /// <summary>Gets or sets the newly detected value.</summary>
    public string? NewValue { get; set; }

    /// <summary>Gets or sets whether the change adds, removes, or updates something.</summary>
    public string Kind { get; set; } = "updated";
}

/// <summary>The difference between stored and freshly detected project metadata.</summary>
public sealed class ProjectRefreshDiff
{
    /// <summary>Gets or sets the project identifier.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the refresh ran.</summary>
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the freshly detected project.</summary>
    public ProjectModel DetectedProject { get; set; } = new();

    /// <summary>Gets or sets the changes.</summary>
    public List<ProjectFieldChange> Changes { get; set; } = [];

    /// <summary>Gets whether anything changed.</summary>
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>Manages added projects, detection, and project-level configuration.</summary>
public interface IProjectService
{
    /// <summary>Gets the current projects ordered by name.</summary>
    IReadOnlyList<ProjectModel> Projects { get; }

    /// <summary>Gets whether projects have been loaded from disk.</summary>
    bool IsInitialized { get; }

    /// <summary>Loads persisted projects.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Scans a folder without adding anything.</summary>
    Task<ProjectScanResult> ScanAsync(
        string rootPath,
        ProjectScanOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a project from a folder, optionally forcing a kind.</summary>
    Task<ProjectModel> AddAsync(
        string rootPath,
        ProjectKind? kind = null,
        CancellationToken cancellationToken = default);

    /// <summary>Adds every candidate found by a scan.</summary>
    Task<IReadOnlyList<ProjectModel>> AddAllAsync(
        string rootPath,
        ProjectScanOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Adds an APK-only project from one or more APK files.</summary>
    Task<ProjectModel> AddApkAsync(
        IReadOnlyList<string> apkPaths,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a project by identifier.</summary>
    ProjectModel? Get(string projectId);

    /// <summary>Persists a modified project.</summary>
    Task SaveAsync(ProjectModel project, CancellationToken cancellationToken = default);

    /// <summary>Removes a project.</summary>
    Task RemoveAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Marks a project as opened.</summary>
    Task MarkOpenedAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Re-runs detection and returns the differences.</summary>
    Task<ProjectRefreshDiff> PreviewRefreshAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Applies accepted refresh changes.</summary>
    Task<ProjectModel> ApplyRefreshAsync(
        ProjectRefreshDiff diff,
        IReadOnlyCollection<string> acceptedFields,
        CancellationToken cancellationToken = default);

    /// <summary>Re-detects a project and applies all changes.</summary>
    Task<ProjectModel> RefreshAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Gets the aapt2 path currently used for inspection.</summary>
    string? Aapt2Path { get; }

    /// <summary>Raised when the project list or a project changes.</summary>
    event EventHandler<ProjectModel>? ProjectChanged;

    /// <summary>Raised when the project list changes.</summary>
    event EventHandler? ProjectsChanged;
}

/// <summary>Builds and validates the commands a run configuration will execute.</summary>
public interface IRunPlanBuilder
{
    /// <summary>Builds a preview of the commands without executing anything.</summary>
    RunPlanPreview BuildPreview(ProjectModel project, RunConfiguration configuration, string? deviceSerial = null);

    /// <summary>Creates the process request for a Gradle build.</summary>
    Core.Processes.ProcessRequest? CreateBuildRequest(ProjectModel project, RunConfiguration configuration);
}

/// <summary>A previewed step in a run plan.</summary>
public sealed class RunPlanPreviewStep
{
    /// <summary>Gets or sets the step name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of step.</summary>
    public RunStepKind Kind { get; set; }

    /// <summary>Gets or sets the resolved command line.</summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the step will run.</summary>
    public bool WillRun { get; set; } = true;

    /// <summary>Gets or sets why the step is skipped.</summary>
    public string? SkipReason { get; set; }
}

/// <summary>The commands and decisions a run will make.</summary>
public sealed class RunPlanPreview
{
    /// <summary>Gets or sets the steps in execution order.</summary>
    public List<RunPlanPreviewStep> Steps { get; set; } = [];

    /// <summary>Gets or sets the artifact glob after placeholder expansion.</summary>
    public string ArtifactGlob { get; set; } = string.Empty;

    /// <summary>Gets or sets validation problems that block the run.</summary>
    public List<RunConfigurationValidationError> Errors { get; set; } = [];

    /// <summary>Gets whether the plan is valid.</summary>
    public bool IsValid => Errors.Count == 0;
}
