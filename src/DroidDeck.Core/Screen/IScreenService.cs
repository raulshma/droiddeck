namespace DroidDeck.Core.Screen;

/// <summary>How scrcpy's window is hosted.</summary>
public enum ScreenHostingMode
{
    /// <summary>Open scrcpy as a borderless companion window positioned over the device's mirror pane.</summary>
    DockedCompanion,

    /// <summary>Open scrcpy as a normal, movable external window.</summary>
    ExternalWindow,
}

/// <summary>The lifecycle of a mirror session.</summary>
public enum ScreenSessionState
{
    /// <summary>The session is starting.</summary>
    Starting,

    /// <summary>The session is running.</summary>
    Running,

    /// <summary>The session stopped normally.</summary>
    Stopped,

    /// <summary>The session failed to start or ended unexpectedly.</summary>
    Failed,
}

/// <summary>A rectangle in desktop coordinates.</summary>
public sealed record ScreenBounds(int X, int Y, int Width, int Height)
{
    /// <summary>Gets whether the rectangle has a positive size.</summary>
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>Options for starting a mirror.</summary>
public sealed record ScreenStartOptions
{
    /// <summary>Gets the hosting mode.</summary>
    public ScreenHostingMode HostingMode { get; init; } = ScreenHostingMode.DockedCompanion;

    /// <summary>Gets the desktop rectangle used for a docked companion window.</summary>
    public ScreenBounds? Bounds { get; init; }

    /// <summary>Gets the maximum video dimension in pixels.</summary>
    public int? MaxSize { get; init; }

    /// <summary>Gets the frame rate limit.</summary>
    public int? MaxFps { get; init; }

    /// <summary>Gets the video bit rate, such as <c>8M</c>.</summary>
    public string? VideoBitRate { get; init; }

    /// <summary>Gets whether the device stays awake while mirroring.</summary>
    public bool StayAwake { get; init; } = true;

    /// <summary>Gets whether clipboard synchronization is enabled.</summary>
    public bool ClipboardSync { get; init; } = true;

    /// <summary>Gets whether audio is captured.</summary>
    public bool Audio { get; init; }
}

/// <summary>An active or recently stopped mirror session.</summary>
public sealed record ScreenSessionInfo
{
    /// <summary>Gets the mirrored device serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets the scrcpy process identifier.</summary>
    public int ProcessId { get; init; }

    /// <summary>Gets the hosting mode in use.</summary>
    public ScreenHostingMode HostingMode { get; init; }

    /// <summary>Gets the session state.</summary>
    public ScreenSessionState State { get; init; } = ScreenSessionState.Starting;

    /// <summary>Gets when the session started.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the exact scrcpy command line.</summary>
    public string CommandLine { get; init; } = string.Empty;

    /// <summary>Gets whether a recording is active for this device.</summary>
    public bool IsRecording { get; init; }

    /// <summary>Gets the active recording file.</summary>
    public string? RecordingPath { get; init; }

    /// <summary>Gets the scrcpy window title.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>Gets a non-fatal notice, such as an external-window fallback.</summary>
    public string? Notice { get; init; }

    /// <summary>Gets whether the session is running.</summary>
    public bool IsRunning => State is ScreenSessionState.Starting or ScreenSessionState.Running;
}

/// <summary>The outcome of a screen operation.</summary>
public sealed record ScreenOperationResult
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets a user-facing summary.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the exact command line, when one ran.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Gets actionable guidance after a failure.</summary>
    public string? Guidance { get; init; }

    /// <summary>Gets the local screenshot or recording path, when one was produced.</summary>
    public string? Path { get; init; }

    /// <summary>Gets a non-fatal notice, such as a hosting-mode fallback.</summary>
    public string? Notice { get; init; }
}

/// <summary>Keys the mirror can send to the device.</summary>
public enum DeviceControlKey
{
    /// <summary>Back.</summary>
    Back,

    /// <summary>Home.</summary>
    Home,

    /// <summary>Recent apps.</summary>
    Recents,

    /// <summary>Volume up.</summary>
    VolumeUp,

    /// <summary>Volume down.</summary>
    VolumeDown,

    /// <summary>Power.</summary>
    Power,

    /// <summary>Menu.</summary>
    Menu,

    /// <summary>Wake the screen.</summary>
    Wake,
}

/// <summary>Moves and focuses the native scrcpy window.</summary>
public interface IScreenWindowController
{
    /// <summary>Moves and resizes the top-level window owned by a process.</summary>
    bool TryMove(int processId, ScreenBounds bounds);

    /// <summary>Brings the window to the foreground.</summary>
    bool TryFocus(int processId);

    /// <summary>Hides the window, used for the off-screen recording window.</summary>
    bool TryHide(int processId);

    /// <summary>Requests a graceful close so scrcpy can finalize recordings.</summary>
    bool TryClose(int processId);
}

/// <summary>
/// Starts, docks, records, and stops scrcpy sessions. Sessions are always torn
/// down when the device disappears, the app shuts down, or the process exits.
/// </summary>
public interface IScreenService : IAsyncDisposable
{
    /// <summary>Gets the active mirror sessions.</summary>
    IReadOnlyList<ScreenSessionInfo> ActiveSessions { get; }

    /// <summary>Gets whether scrcpy was found.</summary>
    bool IsAvailable { get; }

    /// <summary>Gets actionable guidance when scrcpy is missing.</summary>
    string? AvailabilityError { get; }

    /// <summary>Raised when the active session set changes.</summary>
    event EventHandler? SessionsChanged;

    /// <summary>Starts mirroring a device.</summary>
    Task<ScreenOperationResult> StartAsync(
        string serial,
        ScreenStartOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops mirroring and finalizes an active recording.</summary>
    Task<ScreenOperationResult> StopAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Stops every mirror and recording.</summary>
    Task StopAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves the docked companion window after a pane resize or DPI change.</summary>
    Task<ScreenOperationResult> UpdateBoundsAsync(
        string serial,
        ScreenBounds bounds,
        CancellationToken cancellationToken = default);

    /// <summary>Brings the mirror window to the foreground.</summary>
    Task<ScreenOperationResult> FocusAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Sends a hardware key to the device.</summary>
    Task<ScreenOperationResult> PressKeyAsync(
        string serial,
        DeviceControlKey key,
        CancellationToken cancellationToken = default);

    /// <summary>Captures a PNG screenshot and returns its local path.</summary>
    Task<ScreenOperationResult> ScreenshotAsync(
        string serial,
        string? directory = null,
        CancellationToken cancellationToken = default);

    /// <summary>Starts a headless, zero-flicker recording while the mirror keeps running.</summary>
    Task<ScreenOperationResult> StartRecordingAsync(
        string serial,
        string? directory = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops and finalizes the active recording.</summary>
    Task<ScreenOperationResult> StopRecordingAsync(string serial, CancellationToken cancellationToken = default);
}
