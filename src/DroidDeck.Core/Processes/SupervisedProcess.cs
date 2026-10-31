using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace DroidDeck.Core.Processes;

/// <summary>
/// A long-lived child process started without a shell. Standard output and standard error
/// are pumped line by line, standard error is retained in a bounded tail for diagnostics,
/// and the whole process tree is terminated when the process is stopped.
/// </summary>
public sealed class SupervisedProcess : IAsyncDisposable
{
    /// <summary>The number of standard-error lines retained for diagnostics.</summary>
    public const int StderrTailCapacity = 20;

    private static readonly TimeSpan PumpDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultStopGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly Action<ProcessOutputLine>? _output;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly object _tailSync = new();
    private readonly Queue<string> _stderrTail = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;

    private StreamWriter? _stdin;
    private Task? _stopTask;
    private bool _disposed;

    private SupervisedProcess(
        ProcessRequest request,
        Action<ProcessOutputLine>? output,
        TimeProvider timeProvider)
    {
        _output = output;
        _timeProvider = timeProvider;
        _process = new Process { StartInfo = CreateStartInfo(request) };
        try
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException("The process could not be started.");
            }

            ProcessId = _process.Id;
            ExitTask = WaitForExitAsync(_process);
            _stdin = new StreamWriter(_process.StandardInput.BaseStream, Utf8WithoutBom)
            {
                AutoFlush = true,
            };
            _stdoutPump = Task.Run(
                () => PumpAsync(_process.StandardOutput, ProcessOutputChannel.StandardOutput));
            _stderrPump = Task.Run(
                () => PumpAsync(_process.StandardError, ProcessOutputChannel.StandardError));
            Observe(_stdoutPump);
            Observe(_stderrPump);
        }
        catch
        {
            _process.Dispose();
            throw;
        }
    }

    /// <summary>Gets the operating-system process identifier.</summary>
    public int ProcessId { get; }

    /// <summary>Gets a task that completes when the process exits.</summary>
    public Task ExitTask { get; }

    /// <summary>Gets whether the process has exited or has already been stopped.</summary>
    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // A disposed process handle is no longer running.
                return true;
            }
        }
    }

    /// <summary>Gets a snapshot of the most recent standard-error lines.</summary>
    public IReadOnlyList<string> StderrTail
    {
        get
        {
            lock (_tailSync)
            {
                return _stderrTail.ToArray();
            }
        }
    }

    /// <summary>
    /// Starts a process without a shell, with redirected UTF-8 output, no console window,
    /// and the arguments, environment, and working directory from <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The process to start.</param>
    /// <param name="output">Receives each output line as it arrives.</param>
    /// <param name="timeProvider">Supplies timestamps and stop timing; defaults to the system clock.</param>
    public static SupervisedProcess Start(
        ProcessRequest request,
        Action<ProcessOutputLine>? output = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new SupervisedProcess(request, output, timeProvider ?? TimeProvider.System);
    }

    /// <summary>Writes one line to the process's standard input and flushes it.</summary>
    /// <param name="line">The line to write; a newline is appended.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock or the write itself.</param>
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StreamWriter stdin;
            lock (_sync)
            {
                stdin = _stdin ?? throw new InvalidOperationException("The process is not running.");
            }

            await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Gets the standard-error tail formatted as an error-message suffix.</summary>
    /// <returns>An empty string when there is no tail, otherwise a leading newline and one line per entry.</returns>
    public string FormatStderrTail()
    {
        lock (_tailSync)
        {
            return _stderrTail.Count == 0
                ? string.Empty
                : Environment.NewLine + string.Join(Environment.NewLine, _stderrTail);
        }
    }

    /// <summary>
    /// Closes standard input, waits up to <paramref name="grace"/> for a graceful exit,
    /// kills the entire process tree when it is still running, and drains both output pumps.
    /// Calling this method more than once is safe.
    /// </summary>
    /// <param name="grace">How long to wait for a graceful exit before killing the process tree.</param>
    /// <param name="cancellationToken">Checked after shutdown completes.</param>
    public async Task StopAsync(TimeSpan grace, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(grace, TimeSpan.Zero);

        Task stopTask;
        lock (_sync)
        {
            _stopTask ??= StopCoreAsync(grace);
            stopTask = _stopTask;
        }

        await stopTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            await StopAsync(DefaultStopGracePeriod, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down must not throw from a dispose path.
        }

        _writeLock.Dispose();
    }

    private async Task StopCoreAsync(TimeSpan grace)
    {
        try
        {
            await CloseStandardInputAsync().ConfigureAwait(false);
            await TerminateAsync(grace).ConfigureAwait(false);
            try
            {
                await DrainPumpAsync(_stdoutPump).ConfigureAwait(false);
            }
            finally
            {
                await DrainPumpAsync(_stderrPump).ConfigureAwait(false);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task TerminateAsync(TimeSpan grace)
    {
        try
        {
            if (!HasExited)
            {
                var delay = Task.Delay(grace, _timeProvider);
                var completed = await Task.WhenAny(ExitTask, delay).ConfigureAwait(false);
                if (completed != ExitTask && !HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process ended while it was being terminated.
        }

        try
        {
            await ExitTask.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process handle is already gone.
        }
    }

    private async Task CloseStandardInputAsync()
    {
        await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            StreamWriter? stdin;
            lock (_sync)
            {
                stdin = _stdin;
                _stdin = null;
            }

            try
            {
                stdin?.Dispose();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The child already closed its end of the pipe.
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PumpAsync(StreamReader reader, ProcessOutputChannel channel)
    {
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                EmitLine(line, channel);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stream is torn down when the process exits.
        }
    }

    private void EmitLine(string line, ProcessOutputChannel channel)
    {
        if (channel == ProcessOutputChannel.StandardError)
        {
            lock (_tailSync)
            {
                _stderrTail.Enqueue(line);
                while (_stderrTail.Count > StderrTailCapacity)
                {
                    _stderrTail.Dequeue();
                }
            }
        }

        _output?.Invoke(new ProcessOutputLine(channel, line, _timeProvider.GetUtcNow()));
    }

    private async Task DrainPumpAsync(Task pump)
    {
        try
        {
            await pump.WaitAsync(PumpDrainTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A detached grandchild may still hold the pipe; observe rather than hang.
            Observe(pump);
        }
        catch (OperationCanceledException)
        {
            // The pump observed cancellation directly.
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

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

    private static Task WaitForExitAsync(Process process)
    {
        try
        {
            return process.WaitForExitAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            return Task.CompletedTask;
        }
    }
}
