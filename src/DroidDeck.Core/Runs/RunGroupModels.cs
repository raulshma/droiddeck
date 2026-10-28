using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Runs;

/// <summary>The aggregate state of a multi-device run.</summary>
public enum RunGroupState
{
    /// <summary>The group is waiting to start.</summary>
    Queued,

    /// <summary>At least one device is still running.</summary>
    Running,

    /// <summary>Every device finished successfully.</summary>
    Succeeded,

    /// <summary>Some devices succeeded and some failed.</summary>
    PartiallyFailed,

    /// <summary>Every device failed.</summary>
    Failed,

    /// <summary>The group was cancelled.</summary>
    Cancelled,
}

/// <summary>One device inside a multi-device run.</summary>
public sealed class RunGroupDevice
{
    /// <summary>Gets or sets the device serial.</summary>
    public string Serial { get; set; } = string.Empty;

    /// <summary>Gets or sets the device display name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the current child session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the last known device state.</summary>
    public RunGroupState State { get; set; } = RunGroupState.Queued;

    /// <summary>Gets or sets the last error.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets how many attempts have run on this device.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets whether the device failed and can be retried without a rebuild.</summary>
    public bool CanRetry => State is RunGroupState.Failed or RunGroupState.Cancelled;
}

/// <summary>A build-once, install-many run across a set of devices.</summary>
public sealed class RunGroup
{
    /// <summary>Gets or sets the group identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the project identifier.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Gets or sets the project name.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Gets or sets the run configuration identifier.</summary>
    public string ConfigurationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the run configuration name.</summary>
    public string ConfigurationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected device group name, when one was used.</summary>
    public string? GroupName { get; set; }

    /// <summary>Gets or sets the aggregate state.</summary>
    public RunGroupState State { get; set; } = RunGroupState.Queued;

    /// <summary>Gets or sets the per-device children in picker order.</summary>
    public List<RunGroupDevice> Devices { get; set; } = [];

    /// <summary>Gets or sets the artifact built once for the group.</summary>
    public List<string> PrebuiltArtifactFiles { get; set; } = [];

    /// <summary>Gets or sets the package installed on every device.</summary>
    public string? PackageName { get; set; }

    /// <summary>Gets or sets the launched activity.</summary>
    public string? Activity { get; set; }

    /// <summary>Gets or sets the last error.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets when the group started.</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when the group finished.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Gets or sets the total duration.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets whether the group has finished.</summary>
    public bool IsFinished => State is RunGroupState.Succeeded or RunGroupState.PartiallyFailed
        or RunGroupState.Failed or RunGroupState.Cancelled;

    /// <summary>Gets the number of devices that finished successfully.</summary>
    public int SucceededCount => Devices.Count(device => device.State is RunGroupState.Succeeded);

    /// <summary>Gets the number of devices that are still running or queued.</summary>
    public int PendingCount => Devices.Count(device => device.State is RunGroupState.Running or RunGroupState.Queued);

    /// <summary>Gets the number of devices that failed or were cancelled.</summary>
    public int FailedCount => Devices.Count(device => device.State is RunGroupState.Failed or RunGroupState.Cancelled);

    /// <summary>Gets a short aggregate summary.</summary>
    public string Summary => State switch
    {
        RunGroupState.Queued => "Queued.",
        RunGroupState.Running => $"{SucceededCount} of {Devices.Count} device(s) finished, {PendingCount} in progress.",
        RunGroupState.Succeeded => $"All {Devices.Count} device(s) succeeded.",
        RunGroupState.PartiallyFailed => $"{SucceededCount} succeeded, {FailedCount} failed.",
        RunGroupState.Failed => "Every device failed.",
        RunGroupState.Cancelled => "The run was cancelled.",
        _ => string.Empty,
    };
}

/// <summary>A request to run one project configuration across many devices.</summary>
public sealed class MultiDeviceRunRequest
{
    /// <summary>Gets or sets the project.</summary>
    public required ProjectModel Project { get; init; }

    /// <summary>Gets or sets the run configuration.</summary>
    public required RunConfiguration Configuration { get; init; }

    /// <summary>Gets or sets the devices, in picker order.</summary>
    public required IReadOnlyList<DeviceInfo> Devices { get; init; }

    /// <summary>Gets or sets the selected group name for display.</summary>
    public string? GroupName { get; init; }

    /// <summary>Gets or sets whether this is a dry run.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets or sets the run options applied to every child.</summary>
    public RunOptions Options { get; init; } = new();
}

/// <summary>Describes a change to a run group.</summary>
public sealed class RunGroupChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for a group change.</summary>
    public RunGroupChangedEventArgs(RunGroup group) => Group = group;

    /// <summary>Gets the group snapshot.</summary>
    public RunGroup Group { get; }
}

/// <summary>Runs a project configuration once per device, building at most once.</summary>
public interface IMultiDeviceRunService
{
    /// <summary>Gets the in-memory groups, newest first.</summary>
    IReadOnlyList<RunGroup> Groups { get; }

    /// <summary>Runs the project on every selected device.</summary>
    Task<RunGroup> RunAsync(MultiDeviceRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retries failed devices using the already-built artifact.</summary>
    Task<RunGroup> RetryFailedAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Cancels every running child in a group.</summary>
    Task CancelAsync(string groupId, CancellationToken cancellationToken = default);

    /// <summary>Gets a group by identifier.</summary>
    RunGroup? GetGroup(string groupId);

    /// <summary>Raised when a group or one of its children changes.</summary>
    event EventHandler<RunGroupChangedEventArgs>? GroupChanged;
}
