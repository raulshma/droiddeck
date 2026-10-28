namespace DroidDeck.Core.Emulators;

/// <summary>The lifecycle state of an Android virtual device.</summary>
public enum EmulatorAvdState
{
    /// <summary>The AVD is not running.</summary>
    Stopped,

    /// <summary>The emulator process started and Android is still booting.</summary>
    Starting,

    /// <summary>The AVD is running and adb reports the system booted.</summary>
    Running,

    /// <summary>The AVD is running but its state could not be determined.</summary>
    Unknown,
}

/// <summary>A discovered Android virtual device.</summary>
public sealed record EmulatorAvdInfo
{
    /// <summary>Gets the AVD name accepted by the emulator command.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the Android target, such as <c>android-34</c>.</summary>
    public string? Target { get; init; }

    /// <summary>Gets the API level parsed from the target or system image.</summary>
    public int? ApiLevel { get; init; }

    /// <summary>Gets the ABI, such as <c>x86_64</c>.</summary>
    public string? Abi { get; init; }

    /// <summary>Gets the hardware profile name, such as <c>pixel_7</c>.</summary>
    public string? DeviceProfile { get; init; }

    /// <summary>Gets the lifecycle state.</summary>
    public EmulatorAvdState State { get; init; } = EmulatorAvdState.Stopped;

    /// <summary>Gets the adb serial when the AVD is running.</summary>
    public string? Serial { get; init; }

    /// <summary>Gets the snapshot names that exist for this AVD.</summary>
    public IReadOnlyList<string> Snapshots { get; init; } = [];

    /// <summary>Gets a display name that includes the target when available.</summary>
    public string DisplayName => ApiLevel is { } api
        ? $"{Name} (API {api})"
        : Name;

    /// <summary>Gets whether the AVD is currently running.</summary>
    public bool IsRunning => State is EmulatorAvdState.Running or EmulatorAvdState.Starting;
}

/// <summary>Options for launching an emulator.</summary>
public sealed record EmulatorLaunchOptions
{
    /// <summary>Gets whether to ignore the AVD snapshot and boot from scratch.</summary>
    public bool ColdBoot { get; init; }

    /// <summary>Gets whether to wipe user data before booting.</summary>
    public bool WipeData { get; init; }

    /// <summary>Gets whether to show the emulator window. Defaults to <see langword="true"/>.</summary>
    public bool ShowWindow { get; init; } = true;

    /// <summary>Gets how long to wait for the device to finish booting.</summary>
    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>Progress reported by a long-running emulator operation.</summary>
/// <param name="Message">A human-readable status line.</param>
/// <param name="Percent">Optional completion percentage.</param>
public sealed record EmulatorProgress(string Message, double? Percent = null);

/// <summary>The outcome of an emulator management command.</summary>
public sealed record EmulatorOperationResult
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets a user-facing summary.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the exact command that ran, when one ran.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Gets actionable guidance when the operation failed.</summary>
    public string? Guidance { get; init; }
}

/// <summary>Lists, launches, snapshots, wipes, and stops Android virtual devices.</summary>
public interface IEmulatorService
{
    /// <summary>Gets the most recently discovered AVDs.</summary>
    IReadOnlyList<EmulatorAvdInfo> Avds { get; }

    /// <summary>Gets an actionable error when the emulator executable is missing.</summary>
    string? AvailabilityError { get; }

    /// <summary>Gets whether the emulator executable was found.</summary>
    bool IsAvailable => AvailabilityError is null;

    /// <summary>Raised when the AVD snapshot or running state changes.</summary>
    event EventHandler? AvdsChanged;

    /// <summary>Refreshes the AVD list and running state.</summary>
    Task<IReadOnlyList<EmulatorAvdInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Launches an AVD and waits until adb reports it booted.</summary>
    Task<EmulatorOperationResult> LaunchAsync(
        string avdName,
        EmulatorLaunchOptions options,
        IProgress<EmulatorProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Saves a named snapshot on a running AVD.</summary>
    Task<EmulatorOperationResult> SaveSnapshotAsync(
        string avdName,
        string snapshotName,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a named snapshot on a running AVD.</summary>
    Task<EmulatorOperationResult> LoadSnapshotAsync(
        string avdName,
        string snapshotName,
        CancellationToken cancellationToken = default);

    /// <summary>Stops a running AVD.</summary>
    Task<EmulatorOperationResult> StopAsync(
        string avdName,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an AVD's user data and snapshots. The AVD must be stopped.</summary>
    Task<EmulatorOperationResult> WipeDataAsync(
        string avdName,
        CancellationToken cancellationToken = default);
}
