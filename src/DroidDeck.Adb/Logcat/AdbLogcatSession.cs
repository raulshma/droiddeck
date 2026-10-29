using System.Collections.Concurrent;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Logcat;

/// <summary>Owns one long-lived adb logcat process and delivers parsed entries in batches.</summary>
public sealed class AdbLogcatSession : ILogcatSession
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(50);
    private readonly IProcessRunner _runner;
    private readonly Func<string> _adbPathProvider;
    private readonly ILogger _logger;
    private readonly LogcatRequest _request;
    private readonly ILogEnricher? _enricher;
    private readonly BoundedLogBufferStore _buffer;
    private readonly CrashDetector _crashDetector = new();
    private readonly ConcurrentDictionary<int, byte> _activePids = new();
    private readonly List<CrashEvent> _crashes = [];
    private readonly object _crashSync = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _pumpTask;
    private Task? _flushTask;
    private long _deliveredSequence;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile string? _lastError;
    private int _disposed;

    internal AdbLogcatSession(
        IProcessRunner runner,
        Func<string> adbPathProvider,
        LogcatRequest request,
        ILogger logger,
        ILogEnricher? enricher = null)
    {
        _runner = runner;
        _adbPathProvider = adbPathProvider;
        _request = request;
        _logger = logger;
        _enricher = enricher;
        Id = Guid.NewGuid().ToString("N");
        DeviceSerial = request.DeviceSerial;
        PackageName = request.PackageName;
        AppUid = request.AppUid;
        _buffer = new BoundedLogBufferStore(Math.Max(100, request.MaxLines));
        _crashDetector.TargetPackage = request.PackageName;
        _crashDetector.TargetUid = request.AppUid;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DeviceSerial { get; }

    /// <inheritdoc />
    public string? PackageName { get; }

    /// <inheritdoc />
    public int? AppUid { get; }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public bool IsPaused => _paused;

    /// <inheritdoc />
    public int DroppedLines => _buffer.Dropped;

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> Entries => _buffer.Snapshot();

    /// <inheritdoc />
    public IReadOnlyList<CrashEvent> Crashes
    {
        get
        {
            lock (_crashSync)
            {
                return _crashes.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <inheritdoc />
    public event EventHandler<LogEntriesEventArgs>? EntriesAdded;

    /// <inheritdoc />
    public event EventHandler<CrashDetectedEventArgs>? CrashDetected;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public void Pause()
    {
        if (_paused)
        {
            return;
        }

        _paused = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Resume()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts the logcat process and the batched delivery loop.</summary>
    public void Start()
    {
        if (_pumpTask is not null)
        {
            return;
        }

        _running = true;
        _pumpTask = Task.Run(() => PumpAsync(_cts.Token));
        _flushTask = Task.Run(() => FlushLoopAsync(_cts.Token));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the logcat process and waits for it to exit.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_pumpTask is null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            await _pumpTask.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning(exception, "The logcat process for {Serial} did not stop within the timeout.", DeviceSerial);
        }

        if (_flushTask is not null)
        {
            try
            {
                await _flushTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
                // The flush loop is best effort during shutdown.
            }
        }

        _running = false;
        Flush();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        _crashDetector.Reset();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-s", DeviceSerial, "logcat",
            "-v", "threadtime",
            "-v", "year",
            "-v", "uid",
            "-T", "1",
        };
        foreach (var buffer in _request.Buffers)
        {
            arguments.Add("-b");
            arguments.Add(buffer);
        }

        try
        {
            var result = await _runner.RunAsync(
                new ProcessRequest
                {
                    FileName = _adbPathProvider(),
                    Arguments = arguments,
                    MaxOutputCharacters = 8 * 1024 * 1024,
                    UseJobObject = true,
                },
                new DelegateProgress<ProcessOutputLine>(OnOutput),
                cancellationToken).ConfigureAwait(false);
            if (!result.WasCanceled && !result.Succeeded)
            {
                _lastError = string.IsNullOrWhiteSpace(result.CombinedOutput)
                    ? $"logcat exited with code {result.ExitCode}."
                    : result.CombinedOutput.Trim();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _lastError = exception.Message;
            _logger.LogWarning(exception, "Logcat failed for {Serial}.", DeviceSerial);
        }
        finally
        {
            _running = false;
            Flush();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during stop.
        }
    }

    private void OnOutput(ProcessOutputLine line)
    {
        if (line.Stream != ProcessOutputChannel.StandardOutput)
        {
            return;
        }

        var entry = LogcatParser.ParseLine(line.Text, DateTimeOffset.Now);
        if (entry is null)
        {
            return;
        }

        entry.Source = LogSource.Logcat;
        _enricher?.Enrich(entry, DeviceSerial);
        TrackProcess(entry);
        var crash = _crashDetector.Process(entry);
        if (crash is not null)
        {
            lock (_crashSync)
            {
                _crashes.Add(crash);
            }

            CrashDetected?.Invoke(this, new CrashDetectedEventArgs(crash));
        }

        if (!ShouldInclude(entry))
        {
            return;
        }

        if (!string.IsNullOrEmpty(PackageName) && entry.PackageName is null)
        {
            entry.PackageName = PackageName;
        }

        _buffer.Append(entry);
    }

    private bool ShouldInclude(LogEntry entry)
    {
        if (!_request.AppOnly)
        {
            return true;
        }

        if (entry.Message.StartsWith("--------- beginning of", StringComparison.Ordinal))
        {
            return true;
        }

        if (AppUid is { } uid && entry.Uid == uid)
        {
            return true;
        }

        if (entry.Pid is { } pid && _activePids.ContainsKey(pid))
        {
            return true;
        }

        if (entry.PackageName is not null && !string.IsNullOrEmpty(PackageName) &&
            string.Equals(entry.PackageName, PackageName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(PackageName) &&
            (entry.Tag is "am_crash" or "am_anr" or "ActivityManager" or "DEBUG" or "crash_dump64" or "libc") &&
            entry.Message.Contains(PackageName, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private void TrackProcess(LogEntry entry)
    {
        if (!LogcatParser.TryParseActivityManagerEvent(entry, out var eventName, out var fields) || fields.Count < 3)
        {
            return;
        }

        if (eventName is not ("am_proc_start" or "am_proc_bound" or "am_proc_died"))
        {
            return;
        }

        if (!int.TryParse(fields[1], out var pid))
        {
            return;
        }

        var processName = fields[2];
        if (string.IsNullOrEmpty(processName) || string.IsNullOrEmpty(PackageName))
        {
            return;
        }

        var belongs = string.Equals(processName, PackageName, StringComparison.Ordinal) ||
                      processName.StartsWith(PackageName + ":", StringComparison.Ordinal);
        if (!belongs)
        {
            return;
        }

        if (eventName == "am_proc_died")
        {
            _activePids.TryRemove(pid, out _);
        }
        else
        {
            _activePids[pid] = 0;
        }
    }

    private void Flush()
    {
        if (_paused)
        {
            return;
        }

        var snapshot = _buffer.Snapshot();
        if (snapshot.Count == 0)
        {
            return;
        }

        var batch = new List<LogEntry>();
        foreach (var entry in snapshot)
        {
            if (entry.Sequence > _deliveredSequence)
            {
                batch.Add(entry);
            }
        }

        if (batch.Count == 0)
        {
            return;
        }

        _deliveredSequence = batch[^1].Sequence;
        EntriesAdded?.Invoke(this, new LogEntriesEventArgs(batch, _buffer.Dropped));
    }

    private sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public DelegateProgress(Action<T> action) => _action = action;

        public void Report(T value) => _action(value);
    }
}
