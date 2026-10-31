using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly TaskCompletionSource<string?> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SupervisedProcess? _supervised;
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
                return _supervised is { HasExited: false };
            }
        }
    }

    /// <summary>Gets the app identifier reported by the <c>app.started</c> event.</summary>
    public string? AppId => _appId;

    /// <summary>Starts the Flutter process and waits until the app has started on the device.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        SupervisedProcess process;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_supervised is not null)
            {
                throw new InvalidOperationException("The Flutter session was already started.");
            }

            process = SupervisedProcess.Start(_request, HandleOutputLine);
            _supervised = process;
        }

        var exit = process.ExitTask.WaitAsync(cancellationToken);
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
            "The Flutter session exited before the app started." + process.FormatStderrTail());
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
        SupervisedProcess? process;
        string? appId;
        lock (_sync)
        {
            process = _supervised;
            appId = _appId;
        }

        if (process is null)
        {
            return;
        }

        if (appId is not null && !process.HasExited)
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
        SupervisedProcess stdin;
        lock (_sync)
        {
            stdin = _supervised ?? throw new InvalidOperationException("The Flutter session is not running.");
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var json = JsonSerializer.Serialize(new[] { new { id, method, @params = parameters } });
            await stdin.WriteLineAsync(json, cancellationToken).ConfigureAwait(false);

            return await completion.Task.WaitAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void HandleOutputLine(ProcessOutputLine line)
    {
        if (line.Stream == ProcessOutputChannel.StandardError)
        {
            _output?.Invoke(line);
            return;
        }

        HandleStdoutLine(line.Text);
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
        SupervisedProcess? process;
        lock (_sync)
        {
            process = _supervised;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            await process.StopAsync(ExitGracePeriod, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(_supervised, process))
                {
                    _supervised = null;
                    _appId = null;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Emit(string text, ProcessOutputChannel channel) =>
        _output?.Invoke(new ProcessOutputLine(channel, text, DateTimeOffset.UtcNow));

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
