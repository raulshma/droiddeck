using System.Net.Sockets;
using System.Threading.Channels;
using DroidDeck.Adb.Commands;
using DroidDeck.Adb.Protocol;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Tracking;

/// <summary>
/// Tracks devices over a persistent <c>host:track-devices-l</c> socket with a
/// debounced publish pipeline and a CLI fallback while the socket is unavailable.
/// </summary>
public sealed class AdbDeviceTracker : IAdbDeviceTracker
{
    private readonly AdbTrackerOptions _options;
    private readonly IAdbSocketClient _socketClient;
    private readonly AdbCommandExecutor _adb;
    private readonly ILogger<AdbDeviceTracker> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _fallbackGate = new();
    private volatile string _adbExecutable;

    private DeviceInfo[] _devices = [];
    private bool _socketConnected;
    private bool _fallbackSucceeded;
    private string? _lastError;

    private CancellationTokenSource? _cts;
    private Channel<DeviceInfo[]>? _publishChannel;
    private Task? _trackingTask;
    private Task? _publishTask;
    private CancellationTokenSource? _fallbackCts;
    private Task? _fallbackTask;
    private bool _started;
    private int _disposed;

    /// <summary>Creates a tracker.</summary>
    public AdbDeviceTracker(
        IProcessRunner processRunner,
        AdbTrackerOptions? options = null,
        IAdbSocketClient? socketClient = null,
        ILogger<AdbDeviceTracker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _options = options ?? new AdbTrackerOptions();
        _adbExecutable = _options.AdbExecutable;
        _socketClient = socketClient ?? new AdbSocketClient(_options.ConnectTimeout);
        _logger = logger ?? NullLogger<AdbDeviceTracker>.Instance;
        _adb = new AdbCommandExecutor(processRunner, () => _adbExecutable, logger: _logger);
    }

    /// <summary>Changes the adb executable used for bootstrap and fallback commands.</summary>
    public void UpdateAdbExecutable(string adbExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbExecutable);
        _adbExecutable = adbExecutable;
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            lock (_stateGate)
            {
                return _devices;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _socketConnected || _fallbackSucceeded;
            }
        }
    }

    /// <inheritdoc />
    public bool IsUsingProcessFallback
    {
        get
        {
            lock (_stateGate)
            {
                return !_socketConnected && _fallbackSucceeded;
            }
        }
    }

    /// <inheritdoc />
    public string? LastError
    {
        get
        {
            lock (_stateGate)
            {
                return _lastError;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<AdbDevicesChangedEventArgs>? DevicesChanged;

    /// <inheritdoc />
    public event EventHandler<AdbConnectionChangedEventArgs>? ConnectionChanged;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _cts = new CancellationTokenSource();
            _publishChannel = Channel.CreateBounded<DeviceInfo[]>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

            var token = _cts.Token;
            var reader = _publishChannel.Reader;
            _publishTask = Task.Run(() => RunPublishPumpAsync(reader, token), CancellationToken.None);
            _trackingTask = Task.Run(() => RunTrackingLoopAsync(token), CancellationToken.None);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Device tracking started for {Endpoint}.", _options.Endpoint);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? trackingTask;
        Task? publishTask;
        CancellationTokenSource? cts;

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            cts = _cts;
            _cts = null;
            trackingTask = _trackingTask;
            _trackingTask = null;
            publishTask = _publishTask;
            _publishTask = null;
            _publishChannel?.Writer.TryComplete();

            cts?.Cancel();
            CancelFallback();
        }
        finally
        {
            _lifecycle.Release();
        }

        if (trackingTask is not null)
        {
            await AwaitQuietlyAsync(trackingTask).ConfigureAwait(false);
        }

        if (publishTask is not null)
        {
            await AwaitQuietlyAsync(publishTask).ConfigureAwait(false);
        }

        await StopFallbackAsync().ConfigureAwait(false);
        _publishChannel = null;
        cts?.Dispose();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Device tracking stopped.");
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await _socketClient
                .SendAsync(_options.Endpoint, "host:devices-l", cancellationToken)
                .ConfigureAwait(false);
            Publish(AdbDeviceParser.Parse(payload).ToArray());
            MutateConnection(() => _lastError = null);
            return;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MutateConnection(() => _lastError = exception.Message);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(exception, "The adb device-list query failed.");
            }
        }

        if (!_options.EnableProcessFallback)
        {
            return;
        }

        var devices = await TryPollProcessAsync(cancellationToken).ConfigureAwait(false);
        if (devices is not null)
        {
            Publish(devices);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RestartServerAsync(CancellationToken cancellationToken = default)
    {
        _ = await RunAdbCommandAsync("kill-server", cancellationToken).ConfigureAwait(false);
        var started = await RunAdbCommandAsync("start-server", cancellationToken).ConfigureAwait(false);
        if (!started.Succeeded)
        {
            MutateConnection(() => _lastError = DescribeFailure(started, "adb start-server"));
        }

        return started.Succeeded;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async Task RunTrackingLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? error = null;
            try
            {
                await using var session = await _socketClient
                    .TrackDevicesAsync(_options.Endpoint, cancellationToken)
                    .ConfigureAwait(false);

                attempt = 0;
                CancelFallback();
                MutateConnection(() => { _socketConnected = true; _fallbackSucceeded = false; _lastError = null; });
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Connected to the adb server at {Endpoint}.", _options.Endpoint);
                }

                while (true)
                {
                    var frame = await session.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                    if (frame is null)
                    {
                        error = "The adb server closed the device tracking connection.";
                        break;
                    }

                    Publish(AdbDeviceParser.Parse(frame).ToArray());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                error = $"The adb server at {_options.Endpoint} did not respond in time.";
            }
            catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or AdbProtocolException)
            {
                error = exception.Message;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(exception, "Device tracking failed unexpectedly.");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            MutateConnection(() =>
            {
                _socketConnected = false;
                _fallbackSucceeded = false;
                _lastError = error ?? "The adb connection was closed.";
            });

            if (attempt == 0 || attempt % 10 == 0)
            {
                await TryStartServerAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_options.EnableProcessFallback)
            {
                StartFallback();
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Device tracking will retry after: {Error}", error);
            }

            var delay = GetReconnectDelay(attempt);
            attempt++;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        CancelFallback();
        MutateConnection(() => { _socketConnected = false; _fallbackSucceeded = false; });
    }

    private async Task RunPublishPumpAsync(ChannelReader<DeviceInfo[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.TryRead(out var latest))
                {
                    continue;
                }

                while (reader.TryRead(out var newer))
                {
                    latest = newer;
                }

                if (_options.Debounce > TimeSpan.Zero)
                {
                    await Task.Delay(_options.Debounce, cancellationToken).ConfigureAwait(false);
                    while (reader.TryRead(out var newer))
                    {
                        latest = newer;
                    }
                }

                ApplySnapshot(latest);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Publish(DeviceInfo[] devices)
    {
        var channel = _publishChannel;
        if (channel is not null && channel.Writer.TryWrite(devices))
        {
            return;
        }

        if (channel is null)
        {
            ApplySnapshot(devices);
        }
    }

    private void ApplySnapshot(DeviceInfo[] devices)
    {
        lock (_stateGate)
        {
            if (_devices.AsSpan().SequenceEqual(devices))
            {
                return;
            }

            _devices = devices;
        }

        RaiseDevicesChanged(devices);
    }

    private async Task TryStartServerAsync(CancellationToken cancellationToken)
    {
        var result = await RunAdbCommandAsync("start-server", cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("The adb server was started.");
            }

            return;
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("adb start-server failed: {Error}", DescribeFailure(result, "adb start-server"));
        }
    }

    private async Task<AdbCommandResult> RunAdbCommandAsync(string command, CancellationToken cancellationToken)
    {
        return await _adb
            .RunAsync(
                [command],
                _options.ProcessTimeout,
                new AdbCommandOptions { UseJobObject = false },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void StartFallback()
    {
        lock (_fallbackGate)
        {
            if (_fallbackTask is { IsCompleted: false })
            {
                return;
            }

            if (_cts is null)
            {
                return;
            }

            _fallbackCts?.Dispose();
            var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _fallbackCts = fallbackCts;
            _fallbackTask = Task.Run(() => RunFallbackLoopAsync(fallbackCts), CancellationToken.None);
        }
    }

    private void CancelFallback()
    {
        lock (_fallbackGate)
        {
            _fallbackCts?.Cancel();
        }
    }

    private async Task StopFallbackAsync()
    {
        Task? task;
        lock (_fallbackGate)
        {
            _fallbackCts?.Cancel();
            task = _fallbackTask;
        }

        if (task is not null)
        {
            await AwaitQuietlyAsync(task).ConfigureAwait(false);
        }
    }

    private async Task RunFallbackLoopAsync(CancellationTokenSource fallbackCts)
    {
        var cancellationToken = fallbackCts.Token;
        try
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "The adb socket at {Endpoint} is unavailable; falling back to 'adb devices -l'.",
                    _options.Endpoint);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var devices = await TryPollProcessAsync(cancellationToken).ConfigureAwait(false);
                if (devices is not null && !cancellationToken.IsCancellationRequested && !IsSocketConnected)
                {
                    Publish(devices);
                }

                await Task.Delay(_options.ProcessPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            FinishFallback(fallbackCts);
        }
    }

    private async Task<DeviceInfo[]?> TryPollProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _adb
                .RunAsync(["devices", "-l"], _options.ProcessTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                MutateConnection(() =>
                {
                    _fallbackSucceeded = false;
                    _lastError = DescribeFailure(result, "adb devices -l");
                });
                return null;
            }

            MutateConnection(() => { _fallbackSucceeded = true; _lastError = null; });
            return AdbDeviceParser.Parse(result.StandardOutput).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MutateConnection(() =>
            {
                _fallbackSucceeded = false;
                _lastError = exception.Message;
            });
            return null;
        }
    }

    private void FinishFallback(CancellationTokenSource fallbackCts)
    {
        lock (_fallbackGate)
        {
            if (!ReferenceEquals(_fallbackCts, fallbackCts))
            {
                return;
            }

            _fallbackCts = null;
            _fallbackTask = null;
        }

        fallbackCts.Dispose();
        MutateConnection(() => _fallbackSucceeded = false);
    }

    private bool IsSocketConnected
    {
        get
        {
            lock (_stateGate)
            {
                return _socketConnected;
            }
        }
    }

    private TimeSpan GetReconnectDelay(int attempt)
    {
        var delays = _options.ReconnectDelays;
        if (delays.Count == 0)
        {
            return TimeSpan.FromSeconds(1);
        }

        return delays[Math.Min(attempt, delays.Count - 1)];
    }

    private void MutateConnection(Action mutation)
    {
        bool changed;
        lock (_stateGate)
        {
            var wasConnected = _socketConnected || _fallbackSucceeded;
            var wasFallback = !_socketConnected && _fallbackSucceeded;
            var previousError = _lastError;

            mutation();

            var isConnected = _socketConnected || _fallbackSucceeded;
            var isFallback = !_socketConnected && _fallbackSucceeded;
            changed = wasConnected != isConnected
                || wasFallback != isFallback
                || !string.Equals(previousError, _lastError, StringComparison.Ordinal);
        }

        if (changed)
        {
            RaiseConnectionChanged();
        }
    }

    private static string DescribeFailure(AdbCommandResult result, string commandLine)
    {
        if (!string.IsNullOrWhiteSpace(result.StartError))
        {
            return $"{commandLine} could not start: {result.StartError}";
        }

        var detail = result.FailureMessage ?? result.CombinedOutput.Trim();
        return detail.Length == 0
            ? $"{commandLine} exited with code {result.ExitCode}."
            : $"{commandLine} failed: {detail}";
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RaiseDevicesChanged(IReadOnlyList<DeviceInfo> devices)
    {
        try
        {
            DevicesChanged?.Invoke(this, new AdbDevicesChangedEventArgs(devices));
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(exception, "A device-list handler failed.");
            }
        }
    }

    private void RaiseConnectionChanged()
    {
        var args = new AdbConnectionChangedEventArgs(IsConnected, IsUsingProcessFallback, LastError);
        try
        {
            ConnectionChanged?.Invoke(this, args);
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(exception, "A connection-state handler failed.");
            }
        }
    }
}
