using System.ComponentModel;
using System.Globalization;
using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Resources;
using DroidDeck.Core.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Screen;

/// <summary>
/// Runs scrcpy as a docked companion window (or a normal external window) and
/// supervises every process so session end, device disconnect, or app shutdown
/// cannot leave orphan windows or unfinalized recordings.
/// </summary>
public sealed class ScrcpyScreenService : IScreenService
{
    private const string MissingScrcpyGuidance =
        "Install scrcpy 2.0 or newer (winget install Genymobile.scrcpy) or set the scrcpy path " +
        "in Settings > Tools, then retry. Enable USB debugging and authorize this computer on the device.";

    private static readonly TimeSpan StartupProbe = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan GracefulCloseWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WindowRetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultWindowWait = TimeSpan.FromSeconds(2);

    private readonly IProcessRunner _runner;
    private readonly IProcessLauncher _launcher;
    private readonly IScreenWindowController _windows;
    private readonly IResourceGuard _guard;
    private readonly AppPaths _paths;
    private readonly Func<string?> _scrcpyPathProvider;
    private readonly Func<string> _adbPathProvider;
    private readonly Func<ScreenSettings> _settingsProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _windowWait;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ScrcpyScreenService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionContext> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the screen service.</summary>
    public ScrcpyScreenService(
        IProcessRunner runner,
        IProcessLauncher launcher,
        IScreenWindowController windows,
        IResourceGuard guard,
        AppPaths paths,
        Func<string?> scrcpyPathProvider,
        Func<string> adbPathProvider,
        Func<ScreenSettings>? settingsProvider = null,
        TimeSpan? pollInterval = null,
        TimeSpan? windowWait = null,
        TimeProvider? timeProvider = null,
        ILogger<ScrcpyScreenService>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _scrcpyPathProvider = scrcpyPathProvider ?? throw new ArgumentNullException(nameof(scrcpyPathProvider));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _settingsProvider = settingsProvider ?? (static () => new ScreenSettings());
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _windowWait = windowWait ?? DefaultWindowWait;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ScrcpyScreenService>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<ScreenSessionInfo> ActiveSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Values
                    .Select(context => context.Info)
                    .Where(info => info.IsRunning)
                    .OrderBy(info => info.Serial, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => ResolveScrcpy() is not null;

    /// <inheritdoc />
    public string? AvailabilityError => IsAvailable ? null : MissingScrcpyGuidance;

    /// <inheritdoc />
    public event EventHandler? SessionsChanged;

    /// <inheritdoc />
    public async Task<ScreenOperationResult> StartAsync(
        string serial,
        ScreenStartOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= CreateDefaultOptions();

        lock (_gate)
        {
            if (_sessions.TryGetValue(serial, out var existing) && existing.Info.IsRunning)
            {
                return Success($"{serial} is already mirroring.", existing.Info.CommandLine);
            }
        }

        var executable = ResolveScrcpy();
        if (executable is null)
        {
            return Failure($"scrcpy was not found for {serial}.", MissingScrcpyGuidance);
        }

        if (!_guard.TryAcquire(ResourceKind.Mirror, serial, out var lease) || lease is null)
        {
            return Failure(
                $"The mirror limit is reached, so {serial} cannot start.",
                _guard.Explain() + " Stop a mirror and retry.");
        }

        var effective = ApplySettings(options);
        var mode = effective.HostingMode;
        var notice = (string?)null;
        var title = $"DroidDeck - {serial}";
        LaunchedProcess? launched = null;
        IReadOnlyList<string> arguments = [];

        for (var attempt = 0; attempt < 2; attempt++)
        {
            arguments = ScrcpyCommandBuilder.BuildMirrorArguments(serial, effective, title);
            var commandLine = ScrcpyCommandBuilder.ToCommandLine(executable, arguments);
            try
            {
                launched = _launcher.Start(new ProcessRequest
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseJobObject = false,
                });
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
            {
                _logger.LogError(exception, "scrcpy could not start for {Serial}.", serial);
                if (attempt == 0 && mode == ScreenHostingMode.DockedCompanion)
                {
                    mode = ScreenHostingMode.ExternalWindow;
                    effective = effective with { HostingMode = mode, Bounds = null };
                    notice = "Docked hosting failed; the mirror opened in an external window instead.";
                    continue;
                }

                await lease.DisposeAsync().ConfigureAwait(false);
                return Failure(
                    $"scrcpy could not start for {serial}: {exception.Message}",
                    MissingScrcpyGuidance);
            }

            if (!await _launcher
                    .WaitForExitAsync(launched.ProcessId, StartupProbe, cancellationToken)
                    .ConfigureAwait(false))
            {
                break;
            }

            if (attempt == 0 && mode == ScreenHostingMode.DockedCompanion)
            {
                mode = ScreenHostingMode.ExternalWindow;
                effective = effective with { HostingMode = mode, Bounds = null };
                notice = "Docked hosting did not stay open; the mirror opened in an external window instead.";
                continue;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            return Failure(
                $"scrcpy exited immediately for {serial} ({commandLine}).",
                MissingScrcpyGuidance);
        }

        if (launched is null)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return Failure($"scrcpy could not start for {serial}.", MissingScrcpyGuidance);
        }

        var info = new ScreenSessionInfo
        {
            Serial = serial,
            ProcessId = launched.ProcessId,
            HostingMode = mode,
            State = ScreenSessionState.Running,
            StartedAt = _timeProvider.GetUtcNow(),
            CommandLine = launched.CommandLine,
            WindowTitle = title,
            Notice = notice,
        };

        var context = new SessionContext(info, lease);
        lock (_gate)
        {
            _sessions[serial] = context;
        }

        context.Monitor = MonitorSessionAsync(context);
        RaiseChanged();
        if (notice is not null)
        {
            _logger.LogWarning("{Notice}", notice);
        }

        return Success(
            notice ?? $"{serial} is mirroring in {Describe(mode)}.",
            launched.CommandLine,
            notice);
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> StopAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        SessionContext? context;
        lock (_gate)
        {
            _sessions.Remove(serial, out context);
        }

        if (context is null)
        {
            return Success($"{serial} is not mirroring.");
        }

        var recording = await StopRecordingCoreAsync(context).ConfigureAwait(false);
        context.Cancel();
        await TerminateMirrorAsync(context).ConfigureAwait(false);
        await context.Lease.DisposeAsync().ConfigureAwait(false);
        if (context.Monitor is { } monitor)
        {
            await AwaitMonitorAsync(monitor).ConfigureAwait(false);
        }

        context.DisposeCancellation();
        RaiseChanged();
        return Success(
            $"{serial} stopped." + (recording is null ? string.Empty : $" Recording saved to {recording}."),
            context.Info.CommandLine,
            path: recording);
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        string[] serials;
        lock (_gate)
        {
            serials = _sessions.Keys.ToArray();
        }

        foreach (var serial in serials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await StopAsync(serial, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> UpdateBoundsAsync(
        string serial,
        ScreenBounds bounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(bounds);
        SessionContext? context;
        lock (_gate)
        {
            _sessions.TryGetValue(serial, out context);
        }

        if (context is null)
        {
            return Failure($"{serial} is not mirroring.");
        }

        if (!bounds.IsValid)
        {
            return Failure("The mirror bounds are empty.");
        }

        var moved = await WaitForWindowAsync(
            context.Info.ProcessId,
            processId => _windows.TryMove(processId, bounds),
            cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_sessions.TryGetValue(serial, out var current))
            {
                current.Info = current.Info with
                {
                    Notice = moved ? current.Info.Notice : "The mirror could not be docked; use its external window.",
                };
            }
        }

        RaiseChanged();
        return moved
            ? Success($"Moved the {serial} mirror to {bounds.Width}x{bounds.Height}.")
            : Failure(
                $"The {serial} mirror window could not be moved.",
                "The scrcpy window may still be starting. Use the external window or retry after it appears.");
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> FocusAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        SessionContext? context;
        lock (_gate)
        {
            _sessions.TryGetValue(serial, out context);
        }

        if (context is null)
        {
            return Failure($"{serial} is not mirroring.");
        }

        return await WaitForWindowAsync(context.Info.ProcessId, _windows.TryFocus, cancellationToken)
            .ConfigureAwait(false)
            ? Success($"Focused the {serial} mirror.")
            : Failure($"The {serial} mirror window was not found.");
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> PressKeyAsync(
        string serial,
        DeviceControlKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var keyCode = key switch
        {
            DeviceControlKey.Back => "KEYCODE_BACK",
            DeviceControlKey.Home => "KEYCODE_HOME",
            DeviceControlKey.Recents => "KEYCODE_APP_SWITCH",
            DeviceControlKey.VolumeUp => "KEYCODE_VOLUME_UP",
            DeviceControlKey.VolumeDown => "KEYCODE_VOLUME_DOWN",
            DeviceControlKey.Power => "KEYCODE_POWER",
            DeviceControlKey.Menu => "KEYCODE_MENU",
            DeviceControlKey.Wake => "KEYCODE_WAKEUP",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown device control key."),
        };

        var result = await RunAdbAsync(
            ["-s", serial, "shell", "input", "keyevent", keyCode],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? Success($"Sent {keyCode} to {serial}.", result.CommandLine)
            : Failure(
                $"The {keyCode} key could not be sent: {Truncate(result.CombinedOutput)}",
                "Check that the device is still connected and authorized.",
                result.CommandLine);
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> ScreenshotAsync(
        string serial,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var settings = _settingsProvider();
        var targetDirectory = ResolveDirectory(directory, settings.ScreenshotsDirectory, _paths.ScreenshotsDirectory);
        Directory.CreateDirectory(targetDirectory);
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"screenshot-{Sanitize(serial)}-{stamp}.png";
        var localPath = Path.Combine(targetDirectory, fileName);
        var remoteDirectory = "/sdcard/DroidDeck";
        var remotePath = $"{remoteDirectory}/{fileName}";

        var prepare = await RunAdbAsync(
            ["-s", serial, "shell", "mkdir", "-p", remoteDirectory],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (prepare.ExitCode != 0)
        {
            return Failure(
                $"The screenshot directory could not be created on {serial}.",
                Truncate(prepare.CombinedOutput),
                prepare.CommandLine);
        }

        var capture = await RunAdbAsync(
            ["-s", serial, "shell", "screencap", "-p", remotePath],
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        if (capture.ExitCode != 0)
        {
            return Failure(
                $"The screenshot failed on {serial}.",
                Truncate(capture.CombinedOutput),
                capture.CommandLine);
        }

        var pull = await RunAdbAsync(
            ["-s", serial, "pull", remotePath, localPath],
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);
        _ = await RunAdbAsync(
            ["-s", serial, "shell", "rm", "-f", remotePath],
            TimeSpan.FromSeconds(20),
            CancellationToken.None).ConfigureAwait(false);

        if (pull.ExitCode != 0 || !File.Exists(localPath))
        {
            return Failure(
                $"The screenshot could not be copied from {serial}.",
                Truncate(pull.CombinedOutput),
                pull.CommandLine);
        }

        return Success($"Screenshot saved to {localPath}.", pull.CommandLine, path: localPath);
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> StartRecordingAsync(
        string serial,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ObjectDisposedException.ThrowIf(_disposed, this);
        SessionContext? context;
        lock (_gate)
        {
            _sessions.TryGetValue(serial, out context);
            if (context?.Recorder is { Finalized: false })
            {
                return Success($"{serial} is already recording.", path: context.Recorder.Path);
            }
        }

        if (context is null)
        {
            return Failure(
                $"{serial} is not mirroring.",
                "Start the mirror before recording so the recording follows the session lifecycle.");
        }

        var executable = ResolveScrcpy();
        if (executable is null)
        {
            return Failure($"scrcpy was not found for {serial}.", MissingScrcpyGuidance);
        }

        if (!_guard.TryAcquire(ResourceKind.Recording, serial, out var lease) || lease is null)
        {
            return Failure(
                $"The recording limit is reached, so {serial} cannot record.",
                _guard.Explain() + " Stop another recording and retry.");
        }

        var settings = _settingsProvider();
        var targetDirectory = ResolveDirectory(directory, settings.RecordingsDirectory, _paths.RecordingsDirectory);
        Directory.CreateDirectory(targetDirectory);
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(targetDirectory, $"recording-{Sanitize(serial)}-{stamp}.mkv");
        var recordingOptions = new ScreenStartOptions
        {
            MaxSize = settings.MaxSize,
            MaxFps = settings.MaxFps,
            VideoBitRate = settings.VideoBitRate,
            StayAwake = settings.StayAwake,
            Audio = false,
        };
        var arguments = ScrcpyCommandBuilder.BuildRecorderArguments(serial, recordingOptions, path);
        var commandLine = ScrcpyCommandBuilder.ToCommandLine(executable, arguments);
        try
        {
            var launched = _launcher.Start(new ProcessRequest
            {
                FileName = executable,
                Arguments = arguments,
                UseJobObject = false,
            });
            if (await _launcher
                    .WaitForExitAsync(launched.ProcessId, StartupProbe, cancellationToken)
                    .ConfigureAwait(false))
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                return Failure(
                    $"Recording exited immediately for {serial} ({commandLine}).",
                    MissingScrcpyGuidance);
            }

            await HideRecorderWindowAsync(launched.ProcessId, cancellationToken).ConfigureAwait(false);
            var recorder = new RecorderContext(path, launched, lease);
            lock (_gate)
            {
                context.Recorder = recorder;
                context.Info = context.Info with { IsRecording = true, RecordingPath = path };
            }

            recorder.Monitor = MonitorRecorderAsync(context, recorder);
            RaiseChanged();
            return Success($"Recording {serial} to {path}.", launched.CommandLine, path: path);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            _logger.LogError(exception, "The recording process could not start for {Serial}.", serial);
            return Failure(
                $"Recording could not start for {serial}: {exception.Message}",
                MissingScrcpyGuidance);
        }
    }

    /// <inheritdoc />
    public async Task<ScreenOperationResult> StopRecordingAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        SessionContext? context;
        lock (_gate)
        {
            _sessions.TryGetValue(serial, out context);
        }

        if (context is null)
        {
            return Success($"{serial} is not mirroring.");
        }

        var path = await StopRecordingCoreAsync(context).ConfigureAwait(false);
        if (path is null)
        {
            return Success($"{serial} is not recording.");
        }

        RaiseChanged();
        return Success($"Recording finalized at {path}.", path: path);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private ScreenStartOptions CreateDefaultOptions()
    {
        var settings = _settingsProvider();
        var mode = string.Equals(settings.HostingMode, "external", StringComparison.OrdinalIgnoreCase)
            ? ScreenHostingMode.ExternalWindow
            : ScreenHostingMode.DockedCompanion;
        return ApplySettings(new ScreenStartOptions { HostingMode = mode });
    }

    private ScreenStartOptions ApplySettings(ScreenStartOptions options)
    {
        var settings = _settingsProvider();
        return options with
        {
            MaxSize = options.MaxSize ?? settings.MaxSize,
            MaxFps = options.MaxFps ?? settings.MaxFps,
            VideoBitRate = string.IsNullOrWhiteSpace(options.VideoBitRate)
                ? settings.VideoBitRate
                : options.VideoBitRate,
            StayAwake = options.StayAwake || settings.StayAwake,
            ClipboardSync = options.ClipboardSync && settings.ClipboardSync,
            Audio = options.Audio || settings.Audio,
        };
    }

    private async Task MonitorSessionAsync(SessionContext context)
    {
        try
        {
            while (!context.Cancellation.IsCancellationRequested && _launcher.IsRunning(context.Info.ProcessId))
            {
                await Task.Delay(_pollInterval, _timeProvider, context.Cancellation.Token).ConfigureAwait(false);
            }

            if (context.Cancellation.IsCancellationRequested)
            {
                return;
            }

            await FinalizeUnexpectedExitAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The scrcpy monitor failed for {Serial}.", context.Info.Serial);
        }
    }

    private async Task MonitorRecorderAsync(SessionContext session, RecorderContext recorder)
    {
        try
        {
            while (!session.Cancellation.IsCancellationRequested && _launcher.IsRunning(recorder.Launched.ProcessId))
            {
                await Task.Delay(_pollInterval, _timeProvider, session.Cancellation.Token).ConfigureAwait(false);
            }

            if (session.Cancellation.IsCancellationRequested || !recorder.TryFinalize())
            {
                return;
            }

            await recorder.Lease.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(session.Recorder, recorder))
                {
                    session.Info = session.Info with { IsRecording = false };
                }
            }

            RaiseChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The recording monitor failed for {Serial}.", session.Info.Serial);
        }
    }

    private async Task FinalizeUnexpectedExitAsync(SessionContext context)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(context.Info.Serial, out var current) || !ReferenceEquals(current, context))
            {
                return;
            }

            _sessions.Remove(context.Info.Serial);
        }

        var recording = await StopRecordingCoreAsync(context).ConfigureAwait(false);
        context.Cancel();
        await context.Lease.DisposeAsync().ConfigureAwait(false);
        context.DisposeCancellation();
        context.Info = context.Info with
        {
            State = ScreenSessionState.Stopped,
            IsRecording = false,
            Notice = recording is null
                ? "The mirror window closed."
                : $"The mirror window closed; recording saved to {recording}.",
        };
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("The scrcpy session for {Serial} ended.", context.Info.Serial);
        }

        RaiseChanged();
    }

    private async Task<string?> StopRecordingCoreAsync(SessionContext context)
    {
        RecorderContext? recorder;
        lock (_gate)
        {
            recorder = context.Recorder;
        }

        if (recorder is null || !recorder.TryFinalize())
        {
            return null;
        }

        _ = _windows.TryClose(recorder.Launched.ProcessId);
        if (!await _launcher
                .WaitForExitAsync(recorder.Launched.ProcessId, GracefulCloseWait, CancellationToken.None)
                .ConfigureAwait(false))
        {
            _launcher.Terminate(recorder.Launched.ProcessId);
        }

        if (recorder.Monitor is { } monitor)
        {
            await AwaitMonitorAsync(monitor).ConfigureAwait(false);
        }

        await recorder.Lease.DisposeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(context.Recorder, recorder))
            {
                context.Info = context.Info with { IsRecording = false };
            }
        }

        return recorder.Path;
    }

    private async Task TerminateMirrorAsync(SessionContext context)
    {
        if (_windows.TryClose(context.Info.ProcessId))
        {
            if (await _launcher
                    .WaitForExitAsync(context.Info.ProcessId, GracefulCloseWait, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return;
            }
        }

        _launcher.Terminate(context.Info.ProcessId);
    }

    /// <summary>
    /// Retries a window operation until scrcpy has created its window. scrcpy
    /// reports a healthy process well before its SDL window exists, so the first
    /// focus or dock request after a start commonly arrives too early.
    /// </summary>
    private async Task<bool> WaitForWindowAsync(
        int processId,
        Func<int, bool> attempt,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + _windowWait;
        while (true)
        {
            if (attempt(processId))
            {
                return true;
            }

            if (_timeProvider.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(WindowRetryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HideRecorderWindowAsync(int processId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_windows.TryHide(processId))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<ProcessResult> RunAdbAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await _runner.RunAsync(
            new ProcessRequest
            {
                FileName = _adbPathProvider(),
                Arguments = arguments,
                Timeout = timeout,
                MaxOutputCharacters = 512 * 1024,
            },
            output: null,
            cancellationToken).ConfigureAwait(false);

    private string? ResolveScrcpy()
    {
        var configured = _scrcpyPathProvider();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured) ||
                configured.Contains(Path.DirectorySeparatorChar) ||
                configured.Contains(Path.AltDirectorySeparatorChar))
            {
                return File.Exists(configured) ? configured : null;
            }

            return configured;
        }

        return "scrcpy";
    }

    private static string ResolveDirectory(string? requested, string? configured, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return Path.GetFullPath(requested);
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return fallback;
    }

    private static string Sanitize(string serial)
    {
        var characters = serial
            .Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(characters).Trim('-');
    }

    private static string Describe(ScreenHostingMode mode) =>
        mode == ScreenHostingMode.DockedCompanion ? "the docked companion window" : "an external window";

    private static async Task AwaitMonitorAsync(Task monitor)
    {
        try
        {
            await monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no output";
        }

        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300] + "...";
    }

    private static ScreenOperationResult Success(
        string message,
        string? commandLine = null,
        string? notice = null,
        string? path = null) => new()
        {
            Succeeded = true,
            Message = message,
            CommandLine = commandLine,
            Notice = notice,
            Path = path,
        };

    private static ScreenOperationResult Failure(
        string message,
        string? guidance = null,
        string? commandLine = null) => new()
        {
            Succeeded = false,
            Message = message,
            Guidance = guidance,
            CommandLine = commandLine,
        };

    private void RaiseChanged()
    {
        try
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A screen session handler failed.");
        }
    }

    private sealed class SessionContext
    {
        private int _canceled;
        private int _cancellationDisposed;

        public SessionContext(ScreenSessionInfo info, IAsyncDisposable lease)
        {
            Info = info;
            Lease = lease;
        }

        public ScreenSessionInfo Info { get; set; }

        public IAsyncDisposable Lease { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Monitor { get; set; }

        public RecorderContext? Recorder { get; set; }

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _canceled, 1) != 0)
            {
                return;
            }

            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown already completed elsewhere.
            }
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) != 0)
            {
                return;
            }

            Cancellation.Dispose();
        }
    }

    private sealed class RecorderContext
    {
        private int _finalized;

        public RecorderContext(string path, LaunchedProcess launched, IAsyncDisposable lease)
        {
            Path = path;
            Launched = launched;
            Lease = lease;
        }

        public string Path { get; }

        public LaunchedProcess Launched { get; }

        public IAsyncDisposable Lease { get; }

        public Task? Monitor { get; set; }

        public bool Finalized => Volatile.Read(ref _finalized) != 0;

        public bool TryFinalize() => Interlocked.Exchange(ref _finalized, 1) == 0;
    }
}
