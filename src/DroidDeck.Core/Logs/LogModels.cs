namespace DroidDeck.Core.Logs;

/// <summary>The source of a log entry.</summary>
public enum LogSource
{
    /// <summary>Build output.</summary>
    Build,

    /// <summary>Device logcat output.</summary>
    Logcat,

    /// <summary>DroidDeck activity events.</summary>
    Activity,

    /// <summary>Test runner output.</summary>
    Test,

    /// <summary>Application diagnostics.</summary>
    System,
}

/// <summary>One parsed log line.</summary>
public sealed class LogEntry
{
    /// <summary>Gets or sets the monotonic sequence number.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Gets or sets the source.</summary>
    public LogSource Source { get; set; } = LogSource.Logcat;

    /// <summary>Gets or sets the one-letter level, or a longer build level.</summary>
    public string Level { get; set; } = "I";

    /// <summary>Gets or sets the log tag.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Gets or sets the process identifier.</summary>
    public int? Pid { get; set; }

    /// <summary>Gets or sets the thread identifier.</summary>
    public int? Tid { get; set; }

    /// <summary>Gets or sets the user identifier.</summary>
    public int? Uid { get; set; }

    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the original line.</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this line continues the previous entry.</summary>
    public bool IsContinuation { get; set; }

    /// <summary>Gets or sets the logcat buffer name, when known.</summary>
    public string? Buffer { get; set; }

    /// <summary>Gets or sets the package this line belongs to, when known.</summary>
    public string? PackageName { get; set; }

    /// <summary>Gets a display label for the level.</summary>
    public string LevelLabel => Level switch
    {
        "V" => "Verbose",
        "D" => "Debug",
        "I" => "Info",
        "W" => "Warn",
        "E" => "Error",
        "F" or "A" => "Fatal",
        _ => Level,
    };

    /// <summary>Returns a copy of the entry.</summary>
    public LogEntry Clone() => (LogEntry)MemberwiseClone();
}

/// <summary>A crash or ANR detected in a log stream.</summary>
public sealed class CrashEvent
{
    /// <summary>Gets or sets the crash kind.</summary>
    public CrashKind Kind { get; set; } = CrashKind.Crash;

    /// <summary>Gets or sets the package name.</summary>
    public string? PackageName { get; set; }

    /// <summary>Gets or sets the process identifier.</summary>
    public int? Pid { get; set; }

    /// <summary>Gets or sets the short summary shown in the banner.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the extracted stack trace or ANR reason.</summary>
    public string StackTrace { get; set; } = string.Empty;

    /// <summary>Gets or sets when the event was detected.</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Gets or sets the sequence number of the first log line.</summary>
    public long FirstSequence { get; set; }

    /// <summary>Gets or sets the sequence number of the last log line.</summary>
    public long LastSequence { get; set; }

    /// <summary>Gets or sets a stable key used to avoid duplicate banners.</summary>
    public string DedupeKey { get; set; } = string.Empty;
}

/// <summary>The kind of crash event.</summary>
public enum CrashKind
{
    /// <summary>A Java or Kotlin uncaught exception.</summary>
    Crash,

    /// <summary>An application-not-responding event.</summary>
    Anr,

    /// <summary>A native crash or tombstone.</summary>
    Native,
}

/// <summary>Event data for a batch of log entries.</summary>
public sealed class LogEntriesEventArgs : EventArgs
{
    /// <summary>Creates event data for a batch.</summary>
    public LogEntriesEventArgs(IReadOnlyList<LogEntry> entries, int droppedLines)
    {
        Entries = entries;
        DroppedLines = droppedLines;
    }

    /// <summary>Gets the batch.</summary>
    public IReadOnlyList<LogEntry> Entries { get; }

    /// <summary>Gets the total number of dropped lines.</summary>
    public int DroppedLines { get; }
}

/// <summary>Event data for a detected crash.</summary>
public sealed class CrashDetectedEventArgs : EventArgs
{
    /// <summary>Creates event data for a crash.</summary>
    public CrashDetectedEventArgs(CrashEvent crash) => Crash = crash;

    /// <summary>Gets the crash.</summary>
    public CrashEvent Crash { get; }
}

/// <summary>A live log stream for one device.</summary>
public interface ILogcatSession : IDisposable
{
    /// <summary>Gets the session identifier.</summary>
    string Id { get; }

    /// <summary>Gets the device serial.</summary>
    string DeviceSerial { get; }

    /// <summary>Gets the package being followed.</summary>
    string? PackageName { get; }

    /// <summary>Gets the app UID when known.</summary>
    int? AppUid { get; }

    /// <summary>Gets whether the stream is running.</summary>
    bool IsRunning { get; }

    /// <summary>Gets whether delivery to the UI is paused.</summary>
    bool IsPaused { get; }

    /// <summary>Gets the number of lines dropped because the buffer was full.</summary>
    int DroppedLines { get; }

    /// <summary>Gets the current retained entries.</summary>
    IReadOnlyList<LogEntry> Entries { get; }

    /// <summary>Gets the crashes detected so far.</summary>
    IReadOnlyList<CrashEvent> Crashes { get; }

    /// <summary>Gets the last error from the stream.</summary>
    string? LastError { get; }

    /// <summary>Pauses delivery to the UI while continuing to buffer.</summary>
    void Pause();

    /// <summary>Resumes delivery.</summary>
    void Resume();

    /// <summary>Raised when a batch of entries is ready for the UI.</summary>
    event EventHandler<LogEntriesEventArgs>? EntriesAdded;

    /// <summary>Raised when a crash or ANR is detected.</summary>
    event EventHandler<CrashDetectedEventArgs>? CrashDetected;

    /// <summary>Raised when the stream state changes.</summary>
    event EventHandler? StateChanged;
}

/// <summary>Describes a logcat stream to start.</summary>
public sealed class LogcatRequest
{
    /// <summary>Gets or sets the device serial.</summary>
    public required string DeviceSerial { get; init; }

    /// <summary>Gets or sets the package to follow.</summary>
    public string? PackageName { get; init; }

    /// <summary>Gets or sets the app UID, when known.</summary>
    public int? AppUid { get; init; }

    /// <summary>Gets or sets the logcat buffers.</summary>
    public IReadOnlyList<string> Buffers { get; init; } = ["main", "system", "crash", "events"];

    /// <summary>Gets or sets whether logcat is cleared before attaching.</summary>
    public bool ClearBeforeAttach { get; init; }

    /// <summary>Gets or sets whether host-side app filtering is applied.</summary>
    public bool AppOnly { get; init; } = true;

    /// <summary>Gets or sets the maximum number of retained lines.</summary>
    public int MaxLines { get; init; } = 20_000;
}

/// <summary>Starts and tracks logcat streams.</summary>
public interface ILogcatService : IAsyncDisposable
{
    /// <summary>Gets the active sessions.</summary>
    IReadOnlyList<ILogcatSession> Sessions { get; }

    /// <summary>Starts a logcat stream.</summary>
    Task<ILogcatSession> StartAsync(LogcatRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops a logcat stream.</summary>
    Task StopAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Gets a session by identifier.</summary>
    ILogcatSession? GetSession(string sessionId);

    /// <summary>Stops every stream.</summary>
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
