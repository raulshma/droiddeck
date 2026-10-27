using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DroidDeck.App;

/// <summary>Arguments forwarded by a second launch.</summary>
public sealed class ActivationRequestedEventArgs : EventArgs
{
    /// <summary>Creates event data.</summary>
    public ActivationRequestedEventArgs(IReadOnlyList<string> arguments) => Arguments = arguments;

    /// <summary>Gets the forwarded command-line arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }
}

/// <summary>
/// Enforces one running instance per data directory. A second launch sends its
/// arguments over a named pipe; the first instance activates its window.
/// </summary>
public sealed class AppInstanceCoordinator : IDisposable
{
    private const int ForwardAttempts = 30;
    private static readonly TimeSpan ForwardRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly string _pipeName;
    private readonly List<IReadOnlyList<string>> _pendingActivations = [];
    private readonly object _sync = new();
    private readonly ILogger? _logger;
    private bool _disposed;

    private AppInstanceCoordinator(Mutex mutex, string pipeName, ILogger? logger)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _logger = logger;
        _ = Task.Run(() => ListenAsync(_cancellation.Token));
    }

    /// <summary>Raised when another launch asks this instance to activate.</summary>
    public event EventHandler<ActivationRequestedEventArgs>? ActivationRequested;

    /// <summary>
    /// Acquires the primary-instance mutex, or forwards the arguments to the
    /// running instance. Returns <see langword="null"/> when forwarding succeeded.
    /// </summary>
    public static AppInstanceCoordinator? TryAcquireOrForward(
        string identity,
        IReadOnlyList<string> arguments,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        var key = ComputeKey(identity);
        var mutexName = $"Local\\DroidDeck.Instance.{key}";
        var pipeName = $"DroidDeck.Instance.{key}";

        var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        if (createdNew)
        {
            return new AppInstanceCoordinator(mutex, pipeName, logger);
        }

        mutex.Dispose();
        if (!TryForward(pipeName, arguments))
        {
            logger?.LogWarning("Another DroidDeck instance is starting but did not accept the activation request.");
        }

        return null;
    }

    /// <summary>Removes and returns activations received before the shell was ready.</summary>
    public IReadOnlyList<IReadOnlyList<string>> TakePendingActivations()
    {
        lock (_sync)
        {
            var pending = _pendingActivations.ToArray();
            _pendingActivations.Clear();
            return pending;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cancellation.Cancel();
        _cancellation.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was not owned on this thread; releasing is best effort.
        }

        _mutex.Dispose();
    }

    private static bool TryForward(string pipeName, IReadOnlyList<string> arguments)
    {
        for (var attempt = 0; attempt < ForwardAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                client.Connect((int)ForwardRetryDelay.TotalMilliseconds);
                using var writer = new StreamWriter(client, Encoding.UTF8);
                writer.Write(JsonSerializer.Serialize(arguments));
                writer.Flush();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Thread.Sleep(ForwardRetryDelay);
            }
        }

        return false;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 16 * 1024,
                    outBufferSize: 1024);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                string payload;
                try
                {
                    payload = await reader.ReadToEndAsync(readCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger?.LogWarning("The activation pipe read timed out; retrying.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                string[]? arguments;
                try
                {
                    arguments = JsonSerializer.Deserialize<string[]>(payload);
                }
                catch (JsonException exception)
                {
                    _logger?.LogWarning(exception, "The activation request was not valid JSON.");
                    continue;
                }

                if (arguments is null)
                {
                    continue;
                }

                Raise(new ActivationRequestedEventArgs(arguments));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(exception, "The activation pipe failed; retrying.");
                try
                {
                    await Task.Delay(ForwardRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Raise(ActivationRequestedEventArgs args)
    {
        var handler = ActivationRequested;
        if (handler is null)
        {
            lock (_sync)
            {
                _pendingActivations.Add(args.Arguments);
            }

            return;
        }

        try
        {
            handler(this, args);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "An activation request handler failed.");
        }
    }

    private static string ComputeKey(string identity)
    {
        var normalized = identity.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }
}
