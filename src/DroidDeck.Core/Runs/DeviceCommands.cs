using DroidDeck.Core.Processes;

namespace DroidDeck.Core.Runs;

/// <summary>The result of an adb device command.</summary>
public sealed class DeviceCommandResult
{
    /// <summary>Gets or sets whether the command succeeded.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Gets or sets the exact command line.</summary>
    public string CommandLine { get; set; } = string.Empty;

    /// <summary>Gets or sets the combined output.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>Gets or sets the error output.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Gets or sets the exit code.</summary>
    public int ExitCode { get; set; }

    /// <summary>Gets or sets whether the command timed out.</summary>
    public bool TimedOut { get; set; }

    /// <summary>Gets or sets whether the command was cancelled.</summary>
    public bool WasCanceled { get; set; }

    /// <summary>Gets or sets a start failure.</summary>
    public string? StartError { get; set; }

    /// <summary>Gets or sets the parsed launch duration.</summary>
    public TimeSpan? LaunchTime { get; set; }

    /// <summary>Gets or sets the parsed launch status.</summary>
    public string? LaunchStatus { get; set; }
}

/// <summary>Options for installing an APK set.</summary>
public sealed class AdbInstallOptions
{
    /// <summary>Gets or sets whether an existing package is replaced.</summary>
    public bool Replace { get; init; } = true;

    /// <summary>Gets or sets whether test-only APKs are allowed.</summary>
    public bool TestOnly { get; init; } = true;

    /// <summary>Gets or sets whether runtime permissions are granted.</summary>
    public bool GrantPermissions { get; init; }

    /// <summary>Gets or sets whether a version downgrade is allowed.</summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>Gets or sets the Android user to install for.</summary>
    public string? User { get; init; }

    /// <summary>Gets or sets extra arguments.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}

/// <summary>Options for launching an application.</summary>
public sealed class AdbLaunchOptions
{
    /// <summary>Gets or sets the package name.</summary>
    public required string PackageName { get; init; }

    /// <summary>Gets or sets the activity name, including the package prefix or a leading dot.</summary>
    public string? Activity { get; init; }

    /// <summary>Gets or sets the intent action.</summary>
    public string? Action { get; init; }

    /// <summary>Gets or sets the intent data URI.</summary>
    public string? Data { get; init; }

    /// <summary>Gets or sets extra arguments passed to <c>am start</c>.</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>Gets or sets whether to wait for the first frame.</summary>
    public bool Wait { get; init; } = true;

    /// <summary>Gets or sets whether to wait for a debugger.</summary>
    public bool WaitForDebugger { get; init; }
}

/// <summary>Executes adb commands for the run pipeline.</summary>
public interface IDeviceCommandService
{
    /// <summary>Installs one APK or a split set.</summary>
    Task<DeviceCommandResult> InstallAsync(
        string serial,
        IReadOnlyList<string> apkPaths,
        AdbInstallOptions options,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default);

    /// <summary>Launches an application with <c>am start -W</c>.</summary>
    Task<DeviceCommandResult> LaunchAsync(
        string serial,
        AdbLaunchOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Force-stops a package.</summary>
    Task<DeviceCommandResult> ForceStopAsync(string serial, string packageName, CancellationToken cancellationToken = default);

    /// <summary>Uninstalls a package.</summary>
    Task<DeviceCommandResult> UninstallAsync(string serial, string packageName, CancellationToken cancellationToken = default);

    /// <summary>Runs a shell command on the device.</summary>
    Task<DeviceCommandResult> ShellAsync(
        string serial,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves the launcher activity for a package.</summary>
    Task<string?> ResolveActivityAsync(string serial, string packageName, CancellationToken cancellationToken = default);

    /// <summary>Gets the app UID from the package manager.</summary>
    Task<int?> GetPackageUidAsync(string serial, string packageName, CancellationToken cancellationToken = default);

    /// <summary>Gets the free bytes on the data partition.</summary>
    Task<long?> GetFreeStorageAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Clears the device logcat buffers.</summary>
    Task<DeviceCommandResult> ClearLogcatAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Gets the adb executable currently in use.</summary>
    string AdbExecutable { get; }
}
