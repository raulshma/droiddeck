using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DroidDeck.Adb;

namespace DroidDeck.TestSupport;

/// <summary>
/// A scriptable adb server that implements the subset of the wire protocol used by
/// the device tracker.
/// </summary>
public sealed class FakeAdbServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<TrackConnection> _trackConnections = [];
    private TcpListener? _listener;
    private Task? _acceptTask;
    private string[] _devices = [];
    private string? _failureMessage;
    private bool _disposed;

    /// <summary>Starts a server on a loopback port, or on <paramref name="port"/> when non-zero.</summary>
    public FakeAdbServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        var bound = (IPEndPoint)_listener.LocalEndpoint;
        Endpoint = new AdbEndpoint(bound.Address.ToString(), bound.Port);
        _acceptTask = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
    }

    /// <summary>Gets the endpoint clients should use.</summary>
    public AdbEndpoint Endpoint { get; }

    /// <summary>Gets the bound port.</summary>
    public int Port => Endpoint.Port;

    /// <summary>Gets or sets a message returned as a protocol FAIL for every request.</summary>
    public string? FailureMessage
    {
        get
        {
            lock (_gate)
            {
                return _failureMessage;
            }
        }
        set
        {
            lock (_gate)
            {
                _failureMessage = value;
            }
        }
    }

    /// <summary>Gets the current raw <c>devices -l</c> lines.</summary>
    public IReadOnlyList<string> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices;
            }
        }
    }

    /// <summary>Replaces the device list and broadcasts a frame to tracking clients.</summary>
    public async Task SetDevicesAsync(params string[] deviceLines)
    {
        TrackConnection[] connections;
        lock (_gate)
        {
            _devices = deviceLines;
            connections = [.. _trackConnections];
        }

        var payload = BuildPayload(deviceLines);
        foreach (var connection in connections)
        {
            try
            {
                await connection.SendAsync(payload).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                RemoveConnection(connection);
            }
        }
    }

    /// <summary>Stops listening so new connections are refused.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_listener is null)
            {
                return;
            }

            _acceptTask = null;
            _listener.Stop();
            _listener = null;
            foreach (var connection in _trackConnections)
            {
                connection.Dispose();
            }

            _trackConnections.Clear();
        }
    }

    /// <summary>Resumes listening on the same port after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_listener is not null || _disposed)
            {
                return;
            }

            _listener = new TcpListener(IPAddress.Loopback, Endpoint.Port);
            _listener.Start();
            var listener = _listener;
            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        }
    }

    /// <summary>Closes all tracking connections, simulating a server restart.</summary>
    public void DropConnections()
    {
        lock (_gate)
        {
            foreach (var connection in _trackConnections)
            {
                connection.Dispose();
            }

            _trackConnections.Clear();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task? acceptTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            acceptTask = _acceptTask;
            _acceptTask = null;
            _listener?.Stop();
            _listener = null;
            foreach (var connection in _trackConnections)
            {
                connection.Dispose();
            }

            _trackConnections.Clear();
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                string? failure;
                lock (_gate)
                {
                    failure = _failureMessage;
                }

                if (!string.IsNullOrEmpty(failure))
                {
                    await WriteStatusAsync(stream, "FAIL", cancellationToken).ConfigureAwait(false);
                    await WriteFrameAsync(stream, failure, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request, "host:track-devices-l", StringComparison.Ordinal))
                {
                    await HandleTrackAsync(client, stream, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request, "host:devices-l", StringComparison.Ordinal))
                {
                    await WriteStatusAsync(stream, "OKAY", cancellationToken).ConfigureAwait(false);
                    await WriteFrameAsync(stream, BuildPayload(Devices), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.Equals(request, "host:version", StringComparison.Ordinal))
                {
                    await WriteStatusAsync(stream, "OKAY", cancellationToken).ConfigureAwait(false);
                    await WriteFrameAsync(stream, "0029", cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteStatusAsync(stream, "FAIL", cancellationToken).ConfigureAwait(false);
                await WriteFrameAsync(stream, $"unknown host service '{request}'", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    private async Task HandleTrackAsync(
        TcpClient client,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var connection = new TrackConnection(client, stream);
        lock (_gate)
        {
            if (_disposed)
            {
                connection.Dispose();
                return;
            }

            _trackConnections.Add(connection);
        }

        try
        {
            await WriteStatusAsync(stream, "OKAY", cancellationToken).ConfigureAwait(false);
            await connection.SendAsync(BuildPayload(Devices)).ConfigureAwait(false);

            var buffer = new byte[64];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            RemoveConnection(connection);
            connection.Dispose();
        }
    }

    private void RemoveConnection(TrackConnection connection)
    {
        lock (_gate)
        {
            _trackConnections.Remove(connection);
        }
    }

    private static string BuildPayload(IReadOnlyList<string> deviceLines)
    {
        if (deviceLines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join('\n', deviceLines) + "\n";
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (!read)
        {
            return null;
        }

        var lengthText = Encoding.ASCII.GetString(header);
        if (!int.TryParse(lengthText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) ||
            length is < 0 or > 64 * 1024)
        {
            return null;
        }

        var payload = new byte[length];
        if (length > 0 &&
            !await ReadExactlyOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task WriteStatusAsync(
        NetworkStream stream,
        string status,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(status), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes(bytes.Length.ToString("X4", CultureInfo.InvariantCulture));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class TrackConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private int _disposed;

        public TrackConnection(TcpClient client, NetworkStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public async Task SendAsync(string payload)
        {
            await _sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                await WriteFrameAsync(_stream, payload, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _stream.Dispose();
                _client.Dispose();
                _sendGate.Dispose();
            }
        }
    }
}
