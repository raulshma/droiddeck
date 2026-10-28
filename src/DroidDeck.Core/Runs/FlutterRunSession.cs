using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DroidDeck.Core.Processes;

namespace DroidDeck.Core.Runs;

/// <summary>The outcome of a hot reload or hot restart request.</summary>
public sealed record HotReloadResult(bool Succeeded, string Message, TimeSpan Duration);

/// <summary>
/// An attached <c>flutter run --machine</c> session. Commands are JSON-RPC messages written
/// to the process's standard input; events arrive as JSON arrays on standard output.
/// </summary>
public sealed class FlutterRunSession : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProgressTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(5);

    private readonly ProcessRequest _request;
    private readonly Action<ProcessOutputLine>? _output;
    private readonly TimeSpan _startTimeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly TaskCompletionSource<string?> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _lifetime;
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private TaskCompletionSource<string?>? _progress;
    private string? _progressError;
    private string? _appId;
    private int _nextRequestId;
    private bool _disposed;

    /// <summary>Creates a session around a prepared <c>flutter run</c> request.</summary>
    public FlutterRunSession(
        ProcessRequest request,
        Action<ProcessOutputLine>? output = null,
        TimeSpan? startTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _output = output;
        _startTimeout = startTimeout ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>Gets whether the Flutter process is still running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _process is { HasExited: false };
            }
        }
    }

    /// <summary>Gets the app identifier reported by the <c>app.started</c> event.</summary>
    public string? AppId => _appId;

    /// <summary>Starts the Flutter process and waits until the app has started on the device.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        Process process;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null)
            {
                throw new InvalidOperationException("The Flutter session was already started.");
            }

            _lifetime = new CancellationTokenSource();
            process = new Process { StartInfo = CreateStartInfo(_request), EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The Flutter process could not be started.");
            }

            _process = process;
            _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
            _stdoutPump = Task.Run(() => PumpStdoutAsync(process), CancellationToken.None);
            _stderrPump = Task.Run(() => PumpStderrAsync(process), CancellationToken.None);
        }

        var exit = WaitForExitAsync(process, cancellationToken);
        var timeout = Task.Delay(_startTimeout, cancellationToken);
        var completed = await Task.WhenAny(_started.Task, exit, timeout).ConfigureAwait(false);
        if (completed == _started.Task)
        {
            await _started.Task.ConfigureAwait(false);
            return;
        }

        await StopProcessAsync(cancellationToken).ConfigureAwait(false);
        if (completed == timeout)
        {
            throw new TimeoutException(
                $"Flutter did not report that the app started within {_startTimeout.TotalSeconds:F0} seconds.");
        }

        throw new InvalidOperationException(
            "The Flutter session exited before the app started." + FormatStderrTail());
    }

    /// <summary>Sends a hot reload request and waits for the daemon to report progress.</summary>
    public Task<HotReloadResult> HotReloadAsync(CancellationToken cancellationToken = default) =>
        ReloadAsync(fullRestart: false, cancellationToken);

    /// <summary>Sends a hot restart request and waits for the daemon to report progress.</summary>
    public Task<HotReloadResult> HotRestartAsync(CancellationToken cancellationToken = default) =>
        ReloadAsync(fullRestart: true, cancellationToken);

    /// <summary>Stops the app through the daemon and terminates the whole process tree.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Process? process;
        string? appId;
        lock (_sync)
        {
            process = _process;
            appId = _appId;
        }

        if (process is null)
        {
            return;
        }

        if (appId is not null && !HasExited(process))
        {
            try
            {
                await SendCommandAsync("app.stop", new { appId }, cancellationToken)
                    .WaitAsync(ExitGracePeriod, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException)
            {
                // The daemon may already be gone; the process tree is terminated below.
            }
        }

        await StopProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // Shutting down must not throw from a dispose path.
        }

        _lifetime?.Dispose();
        _writeLock.Dispose();
    }

    private async Task<HotReloadResult> ReloadAsync(bool fullRestart, CancellationToken cancellationToken)
    {
        var appId = _appId;
        if (appId is null)
        {
            throw new InvalidOperationException(
                "The Flutter app has not started. Attach the Flutter session before reloading.");
        }

        var started = Stopwatch.StartNew();
        var progress = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _progress = progress;
            _progressError = null;
        }

        try
        {
            var error = await SendCommandAsync(
                    "app.restart",
                    new { appId, fullRestart },
                    cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                return new HotReloadResult(false, DescribeError(error.Value), started.Elapsed);
            }

            string? message;
            try
            {
                message = await progress.Task.WaitAsync(ProgressTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new HotReloadResult(
                    false,
                    "Flutter did not report the result of the request in time.",
                    started.Elapsed);
            }

            string? failure;
            lock (_sync)
            {
                failure = _progressError;
            }

            if (failure is not null)
            {
                return new HotReloadResult(false, failure, started.Elapsed);
            }

            if (!string.IsNullOrWhiteSpace(message) &&
                message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                return new HotReloadResult(false, message, started.Elapsed);
            }

            return new HotReloadResult(
                true,
                string.IsNullOrWhiteSpace(message)
                    ? fullRestart ? "Hot restart finished." : "Hot reload finished."
                    : message,
                started.Elapsed);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_progress, progress))
                {
                    _progress = null;
                }
            }
        }
    }

    private async Task<JsonElement?> SendCommandAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        StreamWriter stdin;
        lock (_sync)
        {
            stdin = _stdin ?? throw new InvalidOperationException("The Flutter session is not running.");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var json = JsonSerializer.Serialize(new[] { new { id, method, @params = parameters } });
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stdin.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await completion.Task.WaitAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task PumpStdoutAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                HandleStdoutLine(line);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stream is torn down when the process exits.
        }
    }

    private async Task PumpStderrAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                lock (_sync)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 20)
                    {
                        _stderrTail.Dequeue();
                    }
                }

                Emit(line, ProcessOutputChannel.StandardError);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stream is torn down when the process exits.
        }
    }

    private void HandleStdoutLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        if (!trimmed.StartsWith('['))
        {
            Emit(line, ProcessOutputChannel.StandardOutput);
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            Emit(line, ProcessOutputChannel.StandardOutput);
            return;
        }

        using (document)
        {
            foreach (var message in document.RootElement.EnumerateArray())
            {
                HandleMessage(message);
            }
        }
    }

    private void HandleMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (message.TryGetProperty("event", out var eventName))
        {
            HandleEvent(eventName.GetString(), message);
            return;
        }

        if (message.TryGetProperty("id", out var id) &&
            id.TryGetInt32(out var requestId) &&
            _pending.TryRemove(requestId, out var completion))
        {
            completion.TrySetResult(
                message.TryGetProperty("error", out var error) ? error.Clone() : null);
        }
    }

    private void HandleEvent(string? name, JsonElement message)
    {
        var parameters = message.TryGetProperty("params", out var value) ? value : default;
        switch (name)
        {
            case "app.started":
                var appId = parameters.ValueKind == JsonValueKind.Object &&
                            parameters.TryGetProperty("appId", out var appIdValue)
                    ? appIdValue.GetString()
                    : null;
                _appId = appId;
                _started.TrySetResult(appId);
                break;

            case "app.progress":
                HandleProgress(parameters);
                break;

            case "app.stop":
                var stopped = new InvalidOperationException("The Flutter app stopped.");
                _started.TrySetException(stopped);
                TaskCompletionSource<string?>? progress;
                lock (_sync)
                {
                    progress = _progress;
                }

                progress?.TrySetException(stopped);
                break;

            case "daemon.showMessage":
                HandleShowMessage(parameters);
                break;

            case "daemon.logMessage":
                if (parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("message", out var logMessage))
                {
                    Emit(logMessage.GetString() ?? string.Empty, ProcessOutputChannel.StandardOutput);
                }

                break;

            default:
                break;
        }
    }

    private void HandleProgress(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var finished = parameters.TryGetProperty("finished", out var finishedValue) &&
                       finishedValue.ValueKind == JsonValueKind.True;
        var message = parameters.TryGetProperty("message", out var messageValue)
            ? messageValue.GetString()
            : null;
        if (finished)
        {
            TaskCompletionSource<string?>? progress;
            lock (_sync)
            {
                progress = _progress;
            }

            progress?.TrySetResult(message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            Emit(message, ProcessOutputChannel.StandardOutput);
        }
    }

    private void HandleShowMessage(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var level = parameters.TryGetProperty("level", out var levelValue) ? levelValue.GetString() : null;
        var text = parameters.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync)
            {
                if (_progress is not null)
                {
                    _progressError = text;
                }
            }

            Emit(text, ProcessOutputChannel.StandardError);
            return;
        }

        Emit(text, ProcessOutputChannel.StandardOutput);
    }

    private async Task StopProcessAsync(CancellationToken cancellationToken)
    {
        Process? process;
        Task? stdoutPump;
        Task? stderrPump;
        lock (_sync)
        {
            process = _process;
            stdoutPump = _stdoutPump;
            stderrPump = _stderrPump;
            _lifetime?.Cancel();
        }

        if (process is null)
        {
            return;
        }

        await CloseStandardInputAsync().ConfigureAwait(false);
        try
        {
            if (!HasExited(process))
            {
                var exit = WaitForExitAsync(process, CancellationToken.None);
                using var grace = new CancellationTokenSource(ExitGracePeriod);
                var delay = Task.Delay(Timeout.InfiniteTimeSpan, grace.Token);
                var completed = await Task.WhenAny(exit, delay).ConfigureAwait(false);
                if (completed != exit && !HasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process ended while it was being terminated.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process handle is already gone.
        }

        await WaitForPumpAsync(stdoutPump).ConfigureAwait(false);
        await WaitForPumpAsync(stderrPump).ConfigureAwait(false);

        lock (_sync)
        {
            _process = null;
            _stdin = null;
            _appId = null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        process.Dispose();
    }

    private async Task CloseStandardInputAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _stdin?.Dispose();
            _stdin = null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var info = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in request.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    private static Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            return process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static async Task WaitForPumpAsync(Task? pump)
    {
        if (pump is null)
        {
            return;
        }

        try
        {
            await pump.WaitAsync(ExitGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A pump that does not finish is abandoned with the process.
        }
    }

    private void Emit(string text, ProcessOutputChannel channel) =>
        _output?.Invoke(new ProcessOutputLine(channel, text, DateTimeOffset.UtcNow));

    private string FormatStderrTail()
    {
        lock (_sync)
        {
            return _stderrTail.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, _stderrTail);
        }
    }

    private static string DescribeError(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? "The Flutter daemon rejected the request.";
        }

        return error.ValueKind == JsonValueKind.String
            ? error.GetString() ?? "The Flutter daemon rejected the request."
            : "The Flutter daemon rejected the request.";
    }
}
