using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Processes;

/// <summary>
/// Runs child processes without a shell, captures bounded structured output, and
/// terminates the whole process tree on timeout or cancellation.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int ReadChunkSize = 4096;
    private const int MaxLineCharacters = 64 * 1024;
    private const string LineTruncationMarker = " ... [line truncated]";
    private const string OutputTruncationMarker = "[output truncated]";
    private static readonly TimeSpan ReaderDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReaderAbandonTimeout = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessRunner> _logger;

    /// <summary>Creates a runner.</summary>
    public ProcessRunner(TimeProvider? timeProvider = null, ILogger<ProcessRunner>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProcessRunner>.Instance;
    }

    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        if (request.MaxOutputCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "MaxOutputCharacters must be greater than zero.");
        }

        if (request.Timeout is { } requestedTimeout && requestedTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be greater than zero.");
        }

        var commandLine = request.ToCommandLine();
        if (cancellationToken.IsCancellationRequested)
        {
            return new ProcessResult { CommandLine = commandLine, WasCanceled = true };
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        try
        {
            if (!process.Start())
            {
                return StartFailure(commandLine, "The process did not start.", stopwatch.Elapsed);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "Could not start process {CommandLine}.", commandLine);
            return StartFailure(commandLine, exception.Message, stopwatch.Elapsed);
        }

        using var job = request.UseJobObject ? ProcessJob.TryAssign(process) : null;
        using var timeoutCancellation = request.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : null;
        using var waitCancellation = timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

        if (request.StandardInput is not null)
        {
            await TryWriteStandardInputAsync(process, request.StandardInput, waitCancellation.Token)
                .ConfigureAwait(false);
        }

        var standardOutput = new BoundedTextBuffer(request.MaxOutputCharacters);
        var standardError = new BoundedTextBuffer(request.MaxOutputCharacters);
        using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stdoutReader = process.StandardOutput;
        var stderrReader = process.StandardError;
        var stdoutTask = StartReader(
            stdoutReader,
            ProcessOutputChannel.StandardOutput,
            standardOutput,
            request,
            output,
            readerCancellation.Token);
        var stderrTask = StartReader(
            stderrReader,
            ProcessOutputChannel.StandardError,
            standardError,
            request,
            output,
            readerCancellation.Token);

        var wasCanceled = false;
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            wasCanceled = cancellationToken.IsCancellationRequested;
            timedOut = !wasCanceled && timeoutCancellation?.IsCancellationRequested == true;
            await KillAsync(process, job).ConfigureAwait(false);
        }

        await DrainReadersAsync(stdoutReader, stderrReader, stdoutTask, stderrTask, readerCancellation)
            .ConfigureAwait(false);
        stopwatch.Stop();

        var exitCode = TryGetExitCode(process);
        var result = new ProcessResult
        {
            CommandLine = commandLine,
            ExitCode = exitCode,
            StandardOutput = standardOutput.ToText(),
            StandardError = standardError.ToText(),
            TimedOut = timedOut,
            WasCanceled = wasCanceled,
            OutputTruncated = standardOutput.Truncated || standardError.Truncated,
            Duration = stopwatch.Elapsed,
        };

        if (timedOut)
        {
            _logger.LogWarning(
                "Process {CommandLine} timed out after {Timeout} and was terminated.",
                commandLine,
                request.Timeout);
        }
        else if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Process {CommandLine} exited with {ExitCode} in {Duration}.",
                commandLine,
                result.ExitCode,
                result.Duration);
        }

        return result;
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = request.CreateNoWindow,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (request.StandardInput is not null)
        {
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var variable in request.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        return startInfo;
    }

    private static async Task TryWriteStandardInputAsync(
        Process process,
        string input,
        CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The child may exit before reading input; that is not a runner failure.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (ObjectDisposedException)
            {
                // The process already shut down.
            }
        }
    }

    /// <summary>
    /// Pumps one pipe on a dedicated thread. Async reads post their continuations to
    /// the thread pool, so a starved pool could leave the reader parked past the drain
    /// grace and silently discard output a finished process already wrote.
    /// </summary>
    private Task StartReader(
        StreamReader reader,
        ProcessOutputChannel stream,
        BoundedTextBuffer buffer,
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => ReadStream(reader, stream, buffer, request, output, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private void ReadStream(
        StreamReader reader,
        ProcessOutputChannel stream,
        BoundedTextBuffer buffer,
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var lineTruncated = false;
        var chars = new char[ReadChunkSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = reader.Read(chars, 0, chars.Length);
                if (read == 0)
                {
                    break;
                }

                var chunk = chars.AsSpan(0, read);
                while (!chunk.IsEmpty)
                {
                    var separator = request.SplitOnCarriageReturn
                        ? chunk.IndexOfAny('\r', '\n')
                        : chunk.IndexOf('\n');
                    if (separator < 0)
                    {
                        AppendLineSegment(line, chunk, ref lineTruncated);
                        break;
                    }

                    var segment = chunk[..separator];
                    if (chunk[separator] == '\n' && !segment.IsEmpty && segment[^1] == '\r')
                    {
                        segment = segment[..^1];
                    }

                    AppendLineSegment(line, segment, ref lineTruncated);
                    EmitLine(line, ref lineTruncated, stream, buffer, request, output);
                    var consumed = separator + 1;
                    if (request.SplitOnCarriageReturn &&
                        chunk[separator] == '\r' &&
                        consumed < chunk.Length &&
                        chunk[consumed] == '\n')
                    {
                        consumed++;
                    }

                    chunk = chunk[consumed..];
                }
            }

            if (line.Length > 0 || lineTruncated)
            {
                EmitLine(line, ref lineTruncated, stream, buffer, request, output);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancellation, or the handle closed while the process was terminated.
        }
    }

    private static void AppendLineSegment(StringBuilder line, ReadOnlySpan<char> segment, ref bool truncated)
    {
        if (segment.IsEmpty)
        {
            return;
        }

        var remaining = MaxLineCharacters - line.Length;
        if (segment.Length <= remaining)
        {
            line.Append(segment);
            return;
        }

        if (remaining > 0)
        {
            line.Append(segment[..remaining]);
        }

        truncated = true;
    }

    private void EmitLine(
        StringBuilder line,
        ref bool lineTruncated,
        ProcessOutputChannel stream,
        BoundedTextBuffer buffer,
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        var text = line.ToString();
        if (lineTruncated)
        {
            text += LineTruncationMarker;
        }

        var stripped = request.StripAnsi ? AnsiText.Strip(text) : text;
        buffer.AppendLine(stripped);
        output?.Report(new ProcessOutputLine(stream, stripped, _timeProvider.GetUtcNow()));
        line.Clear();
        lineTruncated = false;
    }

    private static async Task DrainReadersAsync(
        StreamReader stdoutReader,
        StreamReader stderrReader,
        Task stdoutTask,
        Task stderrTask,
        CancellationTokenSource readerCancellation)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(ReaderDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A detached grandchild may still hold the pipes; stop reading rather than hang.
            readerCancellation.Cancel();
            DisposeReader(stdoutReader);
            DisposeReader(stderrReader);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask)
                    .WaitAsync(ReaderAbandonTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Observe(stdoutTask);
                Observe(stderrTask);
            }
            catch (OperationCanceledException)
            {
                // The reader observed cancellation directly.
            }
        }
        catch (OperationCanceledException)
        {
            // The reader observed cancellation directly.
        }
    }

    /// <summary>Closing the pipe is what unblocks a reader parked in a synchronous read.</summary>
    private static void DisposeReader(StreamReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
            // The reader already released the stream.
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static async Task KillAsync(Process process, ProcessJob? job)
    {
        job?.Terminate();
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // The process may have exited between the check and the kill.
        }

        using var killTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(killTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // The kill did not complete in time; the job handle still cleans up on dispose.
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static ProcessResult StartFailure(string commandLine, string message, TimeSpan duration) => new()
    {
        CommandLine = commandLine,
        StartError = message,
        Duration = duration,
    };

    private sealed class BoundedTextBuffer
    {
        private readonly int _capacity;
        private readonly StringBuilder _builder = new();
        private readonly object _sync = new();

        public BoundedTextBuffer(int capacity) => _capacity = capacity;

        public bool Truncated { get; private set; }

        public void AppendLine(string text)
        {
            lock (_sync)
            {
                if (Truncated)
                {
                    return;
                }

                var remaining = _capacity - _builder.Length;
                var required = text.Length + Environment.NewLine.Length;
                if (required <= remaining)
                {
                    _builder.AppendLine(text);
                    return;
                }

                Truncated = true;
                var marker = OutputTruncationMarker;
                var take = Math.Min(
                    text.Length,
                    Math.Max(0, remaining - Environment.NewLine.Length - marker.Length));
                if (take > 0)
                {
                    _builder.Append(text, 0, take);
                    _builder.AppendLine();
                }

                var space = _capacity - _builder.Length;
                if (space >= marker.Length)
                {
                    _builder.Append(marker);
                }
                else if (space > 0)
                {
                    _builder.Append(marker, 0, space);
                }
            }
        }

        public string ToText()
        {
            lock (_sync)
            {
                return _builder.ToString();
            }
        }
    }
}
