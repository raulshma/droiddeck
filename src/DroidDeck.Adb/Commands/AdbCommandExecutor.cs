using DroidDeck.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Commands;

/// <summary>Why an adb invocation failed.</summary>
public enum AdbFailureKind
{
    /// <summary>The command succeeded.</summary>
    None,

    /// <summary>The adb executable could not be started.</summary>
    StartError,

    /// <summary>The command exceeded its timeout.</summary>
    TimedOut,

    /// <summary>The caller canceled the command.</summary>
    Canceled,

    /// <summary>The process exited with a non-zero code and no more specific signal.</summary>
    ExitCode,

    /// <summary>The device has not authorized this computer.</summary>
    Unauthorized,

    /// <summary>The device is connected but not responding.</summary>
    Offline,

    /// <summary>The requested device serial is unknown to adb.</summary>
    DeviceNotFound,

    /// <summary>No devices or emulators are available.</summary>
    NoDevices,

    /// <summary>A host or device connection could not be established.</summary>
    ConnectionFailure,

    /// <summary>adb reported an error through its output.</summary>
    AdbError,
}

/// <summary>Optional knobs for one adb invocation.</summary>
public sealed record AdbCommandOptions
{
    /// <summary>Gets a progress sink for output lines.</summary>
    public IProgress<ProcessOutputLine>? Output { get; init; }

    /// <summary>Gets the maximum number of captured characters per stream.</summary>
    public int MaxOutputCharacters { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets whether a bare carriage return also terminates a line.</summary>
    public bool SplitOnCarriageReturn { get; init; }

    /// <summary>Gets whether the process tree is placed in a job object.</summary>
    public bool UseJobObject { get; init; } = true;
}

/// <summary>The classified outcome of one adb invocation.</summary>
public sealed record AdbCommandResult
{
    /// <summary>Gets the raw process result.</summary>
    public required ProcessResult Raw { get; init; }

    /// <summary>Gets the failure category, or <see cref="AdbFailureKind.None"/> on success.</summary>
    public AdbFailureKind FailureKind { get; init; }

    /// <summary>Gets a user-facing description when the command failed.</summary>
    public string? FailureMessage { get; init; }

    /// <summary>Gets whether the command succeeded.</summary>
    public bool Succeeded => FailureKind == AdbFailureKind.None;

    /// <summary>Gets the displayed command line.</summary>
    public string CommandLine => Raw.CommandLine;

    /// <summary>Gets captured standard output.</summary>
    public string StandardOutput => Raw.StandardOutput;

    /// <summary>Gets both output streams combined for diagnostics.</summary>
    public string CombinedOutput => Raw.CombinedOutput;

    /// <summary>Gets the process exit code.</summary>
    public int ExitCode => Raw.ExitCode;

    /// <summary>Gets whether the command timed out.</summary>
    public bool TimedOut => Raw.TimedOut;

    /// <summary>Gets whether the caller canceled the command.</summary>
    public bool WasCanceled => Raw.WasCanceled;

    /// <summary>Gets a start failure description, when the process could not be launched.</summary>
    public string? StartError => Raw.StartError;
}

/// <summary>
/// Runs unary adb commands and classifies their outcome with one failure vocabulary.
/// Device-scoped commands always receive the <c>-s &lt;serial&gt;</c> prefix.
/// </summary>
public sealed class AdbCommandExecutor
{
    private readonly IProcessRunner _runner;
    private readonly Func<string> _adbPathProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Creates an executor for a fixed adb executable.</summary>
    public AdbCommandExecutor(
        IProcessRunner runner,
        string adbExecutable,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
        : this(runner, CreateProvider(adbExecutable), timeProvider, logger)
    {
    }

    /// <summary>Creates an executor with a provider for the current adb executable.</summary>
    public AdbCommandExecutor(
        IProcessRunner runner,
        Func<string> adbPathProvider,
        TimeProvider? timeProvider = null,
        ILogger? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Runs a host-scoped adb command.</summary>
    public Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(arguments, timeout, options: null, cancellationToken: cancellationToken);

    /// <summary>Runs a host-scoped adb command with output capture options.</summary>
    public Task<AdbCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        AdbCommandOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(arguments, timeout, options, cancellationToken);

    /// <summary>Runs an adb command scoped to a device serial.</summary>
    public Task<AdbCommandResult> RunForDeviceAsync(
        string serial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(arguments, timeout, options: null, cancellationToken, serial);

    /// <summary>Runs a device-scoped adb command with output capture options.</summary>
    public Task<AdbCommandResult> RunForDeviceAsync(
        string serial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        AdbCommandOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(arguments, timeout, options, cancellationToken, serial);

    private async Task<AdbCommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        AdbCommandOptions? options,
        CancellationToken cancellationToken,
        string? serial = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The command timeout must be positive.");
        }

        options ??= new AdbCommandOptions();
        var effectiveArguments = serial is null
            ? arguments
            : BuildDeviceArguments(serial, arguments);
        var request = new ProcessRequest
        {
            FileName = _adbPathProvider(),
            Arguments = effectiveArguments,
            Timeout = timeout,
            MaxOutputCharacters = options.MaxOutputCharacters,
            SplitOnCarriageReturn = options.SplitOnCarriageReturn,
            UseJobObject = options.UseJobObject,
        };

        var raw = await _runner.RunAsync(request, options.Output, cancellationToken).ConfigureAwait(false);
        var failureKind = Classify(raw, out var failureMessage);
        if (failureKind == AdbFailureKind.StartError)
        {
            _logger.LogWarning("adb could not start: {Error}", raw.StartError);
        }
        else if (failureKind != AdbFailureKind.None && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "adb {CommandLine} failed at {Timestamp} ({FailureKind}): {FailureMessage}",
                raw.CommandLine,
                _timeProvider.GetUtcNow(),
                failureKind,
                failureMessage);
        }

        return new AdbCommandResult
        {
            Raw = raw,
            FailureKind = failureKind,
            FailureMessage = failureMessage,
        };
    }

    /// <summary>Returns the argument list for a device-scoped command, including its serial prefix.</summary>
    /// <param name="serial">The device serial.</param>
    /// <param name="arguments">The adb arguments after the serial.</param>
    public static List<string> BuildDeviceArguments(
        string serial,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(arguments);
        var command = new List<string>(arguments.Count + 2)
        {
            "-s",
            serial,
        };
        command.AddRange(arguments);
        return command;
    }

    private static Func<string> CreateProvider(string adbExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbExecutable);
        return () => adbExecutable;
    }

    private static AdbFailureKind Classify(ProcessResult result, out string? message)
    {
        if (!string.IsNullOrWhiteSpace(result.StartError))
        {
            message = $"adb could not start: {result.StartError}";
            return AdbFailureKind.StartError;
        }

        if (result.TimedOut)
        {
            message = "adb timed out.";
            return AdbFailureKind.TimedOut;
        }

        if (result.WasCanceled)
        {
            message = "adb was canceled.";
            return AdbFailureKind.Canceled;
        }

        if (FindSignal(result.StandardOutput, out var kind, out message) ||
            FindSignal(result.StandardError, out kind, out message))
        {
            return kind;
        }

        if (result.ExitCode != 0)
        {
            var detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput);
            message = detail is null
                ? $"adb exited with code {result.ExitCode}."
                : $"adb exited with code {result.ExitCode}: {detail}";
            return AdbFailureKind.ExitCode;
        }

        message = null;
        return AdbFailureKind.None;
    }

    private static bool FindSignal(string? text, out AdbFailureKind kind, out string? message)
    {
        kind = AdbFailureKind.None;
        message = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var normalized = line.StartsWith("adb: ", StringComparison.OrdinalIgnoreCase)
                ? line["adb: ".Length..].TrimStart()
                : line;
            if (TryMatch(normalized, out kind))
            {
                message = line;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatch(string text, out AdbFailureKind kind)
    {
        if (text.StartsWith("no devices/emulators found", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("no devices found", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.NoDevices;
            return true;
        }

        if (text.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such device", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("device", StringComparison.OrdinalIgnoreCase) &&
             text.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            kind = AdbFailureKind.DeviceNotFound;
            return true;
        }

        if (text.StartsWith("device unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.Unauthorized;
            return true;
        }

        if (text.StartsWith("device offline", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("offline", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device offline", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device still connecting", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device still authorizing", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.Offline;
            return true;
        }

        if (text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot resolve", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unknown host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no route to host", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.ConnectionFailure;
            return true;
        }

        if (text.Equals("ko", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("error ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("failure", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("failed", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("cannot ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("unable to ", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.AdbError;
            return true;
        }

        kind = AdbFailureKind.None;
        return false;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }
}
