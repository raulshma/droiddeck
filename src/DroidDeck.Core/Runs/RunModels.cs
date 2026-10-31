using System.Text.Json.Serialization;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Runs;

/// <summary>The lifecycle state of a run session.</summary>
public enum RunSessionState
{
    /// <summary>The session is waiting to start.</summary>
    Queued,

    /// <summary>Toolchain, project, and device checks run.</summary>
    Preparing,

    /// <summary>The build runs.</summary>
    Building,

    /// <summary>The artifact is being located or inspected.</summary>
    ResolvingArtifact,

    /// <summary>The artifact is being installed.</summary>
    Installing,

    /// <summary>The application is being launched.</summary>
    Launching,

    /// <summary>The application is running and logs are attached.</summary>
    Running,

    /// <summary>The session is stopping.</summary>
    Stopping,

    /// <summary>The session completed successfully.</summary>
    Succeeded,

    /// <summary>The session failed.</summary>
    Failed,

    /// <summary>The session was cancelled.</summary>
    Cancelled,

    /// <summary>Some devices or steps failed while others succeeded.</summary>
    PartiallyFailed,
}

/// <summary>The state of one step.</summary>
public enum RunStepState
{
    /// <summary>The step has not started.</summary>
    Pending,

    /// <summary>The step is running.</summary>
    Running,

    /// <summary>The step succeeded.</summary>
    Succeeded,

    /// <summary>The step failed.</summary>
    Failed,

    /// <summary>The step was skipped.</summary>
    Skipped,

    /// <summary>The step was cancelled.</summary>
    Cancelled,
}

/// <summary>A recovery action offered after a failure.</summary>
public sealed class RecoveryAction
{
    /// <summary>Gets or sets the stable action identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the button label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the explanation shown beside the action.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of recovery.</summary>
    public RecoveryActionKind Kind { get; set; } = RecoveryActionKind.Retry;
}

/// <summary>What a recovery action does.</summary>
public enum RecoveryActionKind
{
    /// <summary>Retries the failed step.</summary>
    Retry,

    /// <summary>Retries the install with the downgrade flag.</summary>
    RetryWithDowngrade,

    /// <summary>Uninstalls the existing package and reinstalls.</summary>
    UninstallAndReinstall,

    /// <summary>Opens the vendor's install permission guidance.</summary>
    EnableInstallViaUsb,

    /// <summary>Frees storage and retries.</summary>
    FreeStorage,

    /// <summary>Retries the install with the test-only flag.</summary>
    RetryWithTestFlag,
}

/// <summary>An explained install failure.</summary>
public sealed class InstallDiagnosis
{
    /// <summary>Gets or sets the adb failure code, such as <c>INSTALL_FAILED_VERSION_DOWNGRADE</c>.</summary>
    public string? Code { get; set; }

    /// <summary>Gets or sets the short title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the explanation.</summary>
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Gets or sets the fix guidance.</summary>
    public string Guidance { get; set; } = string.Empty;

    /// <summary>Gets or sets the offered recovery actions.</summary>
    public List<RecoveryAction> Actions { get; set; } = [];
}

/// <summary>One step in a run session.</summary>
public sealed class RunStep
{
    /// <summary>Gets or sets the stable step identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the step kind.</summary>
    public RunStepKind Kind { get; set; }

    /// <summary>Gets or sets the state.</summary>
    public RunStepState State { get; set; } = RunStepState.Pending;

    /// <summary>Gets or sets when the step started.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Gets or sets when the step ended.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Gets or sets the step duration.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets or sets the exact command line.</summary>
    public string? CommandLine { get; set; }

    /// <summary>Gets or sets the exit code.</summary>
    public int? ExitCode { get; set; }

    /// <summary>Gets or sets the last lines of output.</summary>
    public string? OutputTail { get; set; }

    /// <summary>Gets or sets the failure message.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets why the step was skipped.</summary>
    public string? SkipReason { get; set; }

    /// <summary>Gets or sets the install diagnosis when the step failed.</summary>
    public InstallDiagnosis? Diagnosis { get; set; }

    /// <summary>Gets or sets the recovery actions for the failure.</summary>
    public List<RecoveryAction> RecoveryActions { get; set; } = [];

    /// <summary>Gets or sets parser errors extracted from build output.</summary>
    public List<BuildError> Errors { get; set; } = [];
}

/// <summary>One run of a project configuration on one device.</summary>
public sealed class RunSession
{
    /// <summary>Gets or sets the session identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the project identifier.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Gets or sets the project name.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Gets or sets the project root.</summary>
    public string ProjectRoot { get; set; } = string.Empty;

    /// <summary>Gets or sets the run configuration identifier.</summary>
    public string ConfigurationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the run configuration name.</summary>
    public string ConfigurationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind of run configuration backing this session.</summary>
    public RunConfigurationKind ConfigurationKind { get; set; } = RunConfigurationKind.Gradle;

    /// <summary>Gets whether the session belongs to a React Native run configuration.</summary>
    [JsonIgnore]
    public bool IsReactNative => ConfigurationKind == RunConfigurationKind.ReactNative;

    /// <summary>Gets or sets the target device serial.</summary>
    public string DeviceSerial { get; set; } = string.Empty;

    /// <summary>Gets or sets the target device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the multi-device group identifier, when this session is a child.</summary>
    public string? GroupId { get; set; }

    /// <summary>Gets or sets the session state.</summary>
    public RunSessionState State { get; set; } = RunSessionState.Queued;

    /// <summary>Gets or sets the steps in execution order.</summary>
    public List<RunStep> Steps { get; set; } = [];

    /// <summary>Gets or sets the build output lines.</summary>
    public List<string> BuildOutput { get; set; } = [];

    /// <summary>Gets or sets the log entries captured during the run.</summary>
    public List<LogEntry> LogEntries { get; set; } = [];

    /// <summary>Gets or sets the crash events detected during the run.</summary>
    public List<CrashEvent> Crashes { get; set; } = [];

    /// <summary>Gets or sets when the session started.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when the session ended.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Gets or sets the total duration.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets or sets the resolved APK path.</summary>
    public string? ArtifactPath { get; set; }

    /// <summary>Gets or sets every APK to install, with the base APK first.</summary>
    public List<string> ArtifactFiles { get; set; } = [];

    /// <summary>Gets or sets whether this session was a dry run that executed nothing.</summary>
    public bool IsDryRun { get; set; }

    /// <summary>Gets or sets whether the session can hot reload and hot restart a Flutter app.</summary>
    public bool SupportsHotReload { get; set; }

    /// <summary>Gets or sets the installed package.</summary>
    public string? PackageName { get; set; }

    /// <summary>Gets or sets the launched activity.</summary>
    public string? Activity { get; set; }

    /// <summary>Gets or sets the launch duration reported by <c>am start -W</c>.</summary>
    public TimeSpan? LaunchTime { get; set; }

    /// <summary>Gets or sets the app UID used for log filtering.</summary>
    public int? AppUid { get; set; }

    /// <summary>Gets or sets the logcat session identifier.</summary>
    public string? LogcatSessionId { get; set; }

    /// <summary>Gets or sets the final error message.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets whether the session was loaded from history.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Gets or sets the current state explanation.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Gets the active step, if one is running.</summary>
    public RunStep? ActiveStep => Steps.FirstOrDefault(step => step.State == RunStepState.Running);

    /// <summary>Gets whether the session has finished.</summary>
    public bool IsFinished => State is RunSessionState.Succeeded or RunSessionState.Failed
        or RunSessionState.Cancelled or RunSessionState.PartiallyFailed;
}

/// <summary>
/// A run that has been started. The session is registered and <see cref="IRunSessionService.SessionChanged"/>
/// has already been raised when the service returns this handle; <see cref="Completion"/> finishes when the
/// run reaches a terminal state.
/// </summary>
public sealed record RunHandle(RunSession Session, Task<RunSession> Completion);

/// <summary>A request to run a project configuration.</summary>
public sealed class RunRequest
{
    /// <summary>Gets or sets the project to run.</summary>
    public required ProjectModel Project { get; init; }

    /// <summary>Gets or sets the run configuration.</summary>
    public required RunConfiguration Configuration { get; init; }

    /// <summary>Gets or sets the device to run on.</summary>
    public DeviceInfo? Device { get; init; }

    /// <summary>Gets or sets the device serial when no metadata is available.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Gets or sets whether this is a dry run that executes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets or sets the multi-device group identifier, when this request is a child.</summary>
    public string? GroupId { get; init; }

    /// <summary>Gets or sets the options that control the run.</summary>
    public RunOptions Options { get; init; } = new();
}

/// <summary>Options that control a run.</summary>
public sealed record RunOptions
{
    /// <summary>Gets or sets whether the build is skipped.</summary>
    public bool SkipBuild { get; init; }

    /// <summary>Gets or sets whether install is skipped.</summary>
    public bool SkipInstall { get; init; }

    /// <summary>Gets or sets whether launch is skipped.</summary>
    public bool SkipLaunch { get; init; }

    /// <summary>Gets or sets whether logcat is attached.</summary>
    public bool AttachLogs { get; init; } = true;

    /// <summary>Gets or sets whether a previous install failure should be retried with downgrade allowed.</summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>Gets or sets whether an existing package is uninstalled first.</summary>
    public bool UninstallFirst { get; init; }

    /// <summary>Gets or sets whether a previous install failure should be retried with test-only allowed.</summary>
    public bool AllowTestOnly { get; init; } = true;

    /// <summary>
    /// Gets or sets APK files produced by an earlier build. When set, the resolver
    /// is skipped so a multi-device run can install the same artifact many times.
    /// </summary>
    public IReadOnlyList<string>? PrebuiltArtifactFiles { get; init; }

    /// <summary>Gets or sets the base APK from an earlier build.</summary>
    public string? PrebuiltArtifactPath { get; init; }

    /// <summary>Gets or sets the package name discovered during the first install.</summary>
    public string? PrebuiltPackageName { get; init; }

    /// <summary>Gets or sets the launchable activity discovered during the first install.</summary>
    public string? PrebuiltActivity { get; init; }

    /// <summary>Gets or sets a description of the prebuilt artifact for the session timeline.</summary>
    public string? PrebuiltArtifactDescription { get; init; }

    /// <summary>
    /// Gets or sets whether the caller already holds the per-device operation lock.
    /// Multi-device runs acquire the lock before handing work to the session service.
    /// </summary>
    public bool DeviceLockHeld { get; init; }
}

/// <summary>Describes a session state change.</summary>
public sealed class RunSessionChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for a session change.</summary>
    public RunSessionChangedEventArgs(RunSession session) => Session = session;

    /// <summary>Gets the session snapshot.</summary>
    public RunSession Session { get; }
}

/// <summary>Describes a line of output from a running session.</summary>
public sealed class RunOutputEventArgs : EventArgs
{
    /// <summary>Creates event data for output.</summary>
    public RunOutputEventArgs(string sessionId, string stepId, string line, bool isError)
    {
        SessionId = sessionId;
        StepId = stepId;
        Line = line;
        IsError = isError;
    }

    /// <summary>Gets the session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Gets the step identifier.</summary>
    public string StepId { get; }

    /// <summary>Gets the output line.</summary>
    public string Line { get; }

    /// <summary>Gets whether the line came from standard error.</summary>
    public bool IsError { get; }
}

/// <summary>Orchestrates run sessions.</summary>
public interface IRunSessionService
{
    /// <summary>Gets the in-memory sessions, newest first.</summary>
    IReadOnlyList<RunSession> Sessions { get; }

    /// <summary>
    /// Starts a run, registering the session and raising <see cref="SessionChanged"/> before returning.
    /// </summary>
    /// <param name="request">The run request.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The started session and a completion task that finishes when the run reaches a terminal state.</returns>
    RunHandle Start(RunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts a run and returns after it reaches a terminal state.</summary>
    Task<RunSession> RunAsync(RunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets a session by identifier.</summary>
    RunSession? GetSession(string sessionId);

    /// <summary>Cancels a running session and terminates its processes.</summary>
    Task CancelAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Stops a running session and detaches logs.</summary>
    Task StopAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Restarts the application without rebuilding.</summary>
    Task RestartAppAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Attaches a Flutter session when needed and performs a hot reload.</summary>
    Task<HotReloadResult?> HotReloadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Attaches a Flutter session when needed and performs a hot restart.</summary>
    Task<HotReloadResult?> HotRestartAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Applies a recovery action to a failed step.</summary>
    Task<RunSession> ApplyRecoveryAsync(
        string sessionId,
        RecoveryActionKind action,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a session from the in-memory list.</summary>
    void RemoveSession(string sessionId);

    /// <summary>Raised when a session changes state or a step updates.</summary>
    event EventHandler<RunSessionChangedEventArgs>? SessionChanged;

    /// <summary>Raised when a session produces output.</summary>
    event EventHandler<RunOutputEventArgs>? OutputReceived;

    /// <summary>Raised when a batch of logcat entries arrives for a session.</summary>
    event EventHandler<LogEntriesEventArgs>? LogEntriesReceived;

    /// <summary>Raised when a crash or ANR is detected for a session.</summary>
    event EventHandler<CrashDetectedEventArgs>? CrashDetected;
}
