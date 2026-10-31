using DroidDeck.Core.Processes;

namespace DroidDeck.Core.Toolbox;

/// <summary>Filters installed applications by their install kind.</summary>
public enum AppListFilter
{
    /// <summary>Only applications installed by the user.</summary>
    User,

    /// <summary>Only applications shipped with the system image.</summary>
    System,

    /// <summary>Every installed application.</summary>
    All,
}

/// <summary>An application installed on a device.</summary>
public sealed record ToolboxApp
{
    /// <summary>Gets the package name.</summary>
    public required string PackageName { get; init; }

    /// <summary>Gets a display label. Falls back to the package name when no label was read.</summary>
    public string Label => string.IsNullOrWhiteSpace(FriendlyName) ? PackageName : FriendlyName;

    /// <summary>Gets the optional human-readable application name.</summary>
    public string? FriendlyName { get; init; }

    /// <summary>Gets whether the application is part of the system image.</summary>
    public bool IsSystem { get; init; }

    /// <summary>Gets whether the application is enabled for the current user.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Gets the installed version name, when known.</summary>
    public string? VersionName { get; init; }

    /// <summary>Gets the package kind label used by the UI.</summary>
    public string KindLabel => IsSystem ? "System" : "User";
}

/// <summary>The outcome of a toolbox command.</summary>
public sealed record ToolboxCommandResult
{
    /// <summary>Gets whether the command succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets a short user-facing summary.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the exact adb command that ran or would have run.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Gets the combined command output.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Gets the process exit code, or -1 when no process ran.</summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>Gets whether the command timed out.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Gets whether the caller canceled the command.</summary>
    public bool WasCanceled { get; init; }

    /// <summary>Gets whether the command was blocked because confirmation was missing.</summary>
    public bool Blocked { get; init; }

    /// <summary>Gets whether the operation changed device state and required confirmation.</summary>
    public bool IsDestructive { get; init; }

    /// <summary>Creates a result from a process invocation.</summary>
    /// <param name="result">The raw process result.</param>
    /// <param name="succeeded">
    /// Whether the classified adb invocation succeeded. This can differ from
    /// <paramref name="result"/> when adb reports a failure in its output while
    /// exiting with code zero.
    /// </param>
    /// <param name="message">A short user-facing summary.</param>
    /// <param name="isDestructive">Whether the operation changed device state.</param>
    public static ToolboxCommandResult FromProcess(
        ProcessResult result,
        bool succeeded,
        string message,
        bool isDestructive = false) => new()
        {
            Succeeded = succeeded,
            Message = message,
            CommandLine = result.CommandLine,
            Output = result.CombinedOutput,
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            WasCanceled = result.WasCanceled,
            IsDestructive = isDestructive,
        };

    /// <summary>Creates a failure result that did not run a process.</summary>
    public static ToolboxCommandResult Failure(string message, string? commandLine = null, bool blocked = false) => new()
    {
        Message = message,
        CommandLine = commandLine,
        Blocked = blocked,
    };
}

/// <summary>A runtime permission and its current grant state.</summary>
public sealed record PermissionState
{
    /// <summary>Gets the fully qualified permission name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets whether the permission is currently granted.</summary>
    public bool IsGranted { get; init; }

    /// <summary>Gets whether the permission is a runtime (dangerous) permission.</summary>
    public bool IsRuntime { get; init; } = true;

    /// <summary>Gets the short permission label.</summary>
    public string ShortName => Name.StartsWith("android.permission.", StringComparison.Ordinal)
        ? Name["android.permission.".Length..]
        : Name;

    /// <summary>Gets the grant label used by the UI.</summary>
    public string GrantLabel => IsGranted ? "Granted" : "Denied";
}

/// <summary>One shell command and its result, retained for history.</summary>
public sealed record ShellHistoryEntry
{
    /// <summary>Gets the command text entered by the user.</summary>
    public required string Command { get; init; }

    /// <summary>Gets the exact adb command line.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Gets the combined output.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Gets whether the command succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the exit code.</summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>Gets when the command ran.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets whether the command was blocked by the safety policy.</summary>
    public bool Blocked { get; init; }
}

/// <summary>The direction of a port mapping.</summary>
public enum PortDirection
{
    /// <summary>A host-to-device forward, created with <c>adb forward</c>.</summary>
    Forward,

    /// <summary>A device-to-host reverse, created with <c>adb reverse</c>.</summary>
    Reverse,
}

/// <summary>A forward or reverse port mapping.</summary>
public sealed record PortMapping
{
    /// <summary>Gets the mapping direction.</summary>
    public PortDirection Direction { get; init; }

    /// <summary>Gets the device serial that owns the mapping.</summary>
    public string Serial { get; init; } = string.Empty;

    /// <summary>Gets the local socket specification.</summary>
    public string Local { get; init; } = string.Empty;

    /// <summary>Gets the remote socket specification.</summary>
    public string Remote { get; init; } = string.Empty;

    /// <summary>Gets a display label such as <c>tcp:8080 → tcp:8080</c>.</summary>
    public string Display => Direction == PortDirection.Forward
        ? $"{Local} → {Remote}"
        : $"{Local} ← {Remote}";
}

/// <summary>
/// Application, permission, shell, and port operations for a device toolbox.
/// Destructive operations return the exact command and require an explicit
/// <c>confirmed</c> flag before they execute.
/// </summary>
public interface IDeviceToolboxService
{
    /// <summary>Gets the retained shell command history, newest first.</summary>
    IReadOnlyList<ShellHistoryEntry> ShellHistory { get; }

    /// <summary>Lists installed applications.</summary>
    Task<IReadOnlyList<ToolboxApp>> ListAppsAsync(
        string serial,
        AppListFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>Uninstalls an application.</summary>
    Task<ToolboxCommandResult> UninstallAppAsync(
        string serial,
        string packageName,
        bool confirmed,
        CancellationToken cancellationToken = default);

    /// <summary>Clears all application data.</summary>
    Task<ToolboxCommandResult> ClearAppDataAsync(
        string serial,
        string packageName,
        bool confirmed,
        CancellationToken cancellationToken = default);

    /// <summary>Force-stops an application.</summary>
    Task<ToolboxCommandResult> ForceStopAppAsync(
        string serial,
        string packageName,
        bool confirmed,
        CancellationToken cancellationToken = default);

    /// <summary>Opens the system application-info screen.</summary>
    Task<ToolboxCommandResult> OpenAppInfoAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    /// <summary>Lists runtime permissions for an application.</summary>
    Task<IReadOnlyList<PermissionState>> ListPermissionsAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default);

    /// <summary>Grants or revokes a runtime permission.</summary>
    Task<ToolboxCommandResult> SetPermissionAsync(
        string serial,
        string packageName,
        string permission,
        bool grant,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a shell command. Destructive commands are blocked unless
    /// <paramref name="allowDestructive"/> is set after explicit confirmation.
    /// </summary>
    Task<ToolboxCommandResult> RunShellAsync(
        string serial,
        string command,
        bool allowDestructive = false,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the shell history.</summary>
    void ClearShellHistory();

    /// <summary>Lists forward or reverse port mappings.</summary>
    Task<IReadOnlyList<PortMapping>> ListPortsAsync(
        string serial,
        PortDirection direction,
        CancellationToken cancellationToken = default);

    /// <summary>Adds a port mapping.</summary>
    Task<ToolboxCommandResult> AddPortAsync(
        string serial,
        PortDirection direction,
        string local,
        string remote,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one port mapping.</summary>
    Task<ToolboxCommandResult> RemovePortAsync(
        string serial,
        PortDirection direction,
        string local,
        string remote,
        CancellationToken cancellationToken = default);

    /// <summary>Removes every mapping in one direction.</summary>
    Task<ToolboxCommandResult> RemoveAllPortsAsync(
        string serial,
        PortDirection direction,
        bool confirmed,
        CancellationToken cancellationToken = default);
}
