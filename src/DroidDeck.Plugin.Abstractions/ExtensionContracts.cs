namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// A command that can be executed from menus and the command palette.
/// </summary>
public interface ICommand
{
    /// <summary>Gets the stable command identifier.</summary>
    string Id { get; }

    /// <summary>Gets the display title.</summary>
    string Title { get; }

    /// <summary>Gets a description used by search.</summary>
    string Description { get; }

    /// <summary>Gets additional search keywords.</summary>
    IReadOnlyList<string> Keywords { get; }

    /// <summary>Executes the command.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Context supplied to a project detector.
/// </summary>
/// <param name="ProjectRoot">The folder being inspected.</param>
public sealed record ProjectDetectionContext(string ProjectRoot);

/// <summary>
/// Evidence-backed result of a project detector.
/// </summary>
/// <param name="Kind">The detected project kind.</param>
/// <param name="DisplayName">The human-readable kind name.</param>
/// <param name="Evidence">The files or values that justified the detection.</param>
public sealed record ProjectDetection(
    string Kind,
    string DisplayName,
    IReadOnlyList<string> Evidence);

/// <summary>
/// Detects a project type in a folder.
/// </summary>
public interface IProjectDetector
{
    /// <summary>Gets the stable detector identifier.</summary>
    string Id { get; }

    /// <summary>Gets the detector priority; lower values run first.</summary>
    int Priority { get; }

    /// <summary>Detects a project, returning <see langword="null"/> when it does not apply.</summary>
    Task<ProjectDetection?> DetectAsync(ProjectDetectionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Context supplied to a device action.
/// </summary>
/// <param name="Serial">The target device serial.</param>
/// <param name="Model">The device model, when known.</param>
public sealed record DeviceActionContext(string Serial, string? Model);

/// <summary>
/// An action that can run against a device.
/// </summary>
public interface IDeviceAction
{
    /// <summary>Gets the stable action identifier.</summary>
    string Id { get; }

    /// <summary>Gets the display name.</summary>
    string Name { get; }

    /// <summary>Gets whether the action must be confirmed before running.</summary>
    bool RequiresConfirmation { get; }

    /// <summary>Executes the action.</summary>
    Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Context supplied to a run step.
/// </summary>
/// <param name="ProjectRoot">The project root directory.</param>
/// <param name="DeviceSerial">The target device, if any.</param>
public sealed record RunStepContext(string ProjectRoot, string? DeviceSerial);

/// <summary>
/// The outcome of a run step.
/// </summary>
/// <param name="Succeeded">Whether the step succeeded.</param>
/// <param name="Summary">A short outcome summary.</param>
public sealed record RunStepResult(bool Succeeded, string? Summary);

/// <summary>
/// A step in the run pipeline. Built-in and plugin steps share this contract.
/// </summary>
public interface IRunStep
{
    /// <summary>Gets the stable step identifier.</summary>
    string Id { get; }

    /// <summary>Executes the step.</summary>
    Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken cancellationToken);
}
