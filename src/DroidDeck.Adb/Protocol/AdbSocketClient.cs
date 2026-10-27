using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace DroidDeck.Adb.Protocol;

/// <summary>The default TCP implementation of the adb server protocol.</summary>
public sealed class AdbSocketClient : IAdbSocketClient
{
    private const int StatusLength = 4;
    private const int MaxFrameLength = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _connectTimeout;

    /// <summary>Creates a client.</summary>
    public AdbSocketClient(TimeSpan? connectTimeout = null) =>
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;

    /// <inheritdoc />
    public async Task<IAdbTrackSession> TrackDevicesAsync(
        AdbEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var (client, stream) = await ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            await SendRequestAsync(stream, "host:track-devices-l", cancellationToken).ConfigureAwait(false);
            await ReadOkayAsync(stream, "host:track-devices-l", cancellationToken).ConfigureAwait(false);
            return new AdbTrackSession(client, stream);
        }
        catch
        {
            stream.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> SendAsync(
        AdbEndpoint endpoint,
        string request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);

        using var client = new TcpClient { NoDelay = true };
        var stream = await ConnectStreamAsync(client, endpoint, cancellationToken).ConfigureAwait(false);
        using (stream)
        {
            await SendRequestAsync(stream, request, cancellationToken).ConfigureAwait(false);
            await ReadOkayAsync(stream, request, cancellationToken).ConfigureAwait(false);
            return await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false)
                ?? throw new AdbProtocolException($"The adb server closed the connection after '{request}'.");
        }
    }

    /// <summary>Formats a request with its 4-hex-digit length prefix.</summary>
    public static string FormatRequest(string request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        var length = Encoding.UTF8.GetByteCount(request);
        if (length > ushort.MaxValue)
        {
            throw new ArgumentException("The adb request is too long.", nameof(request));
        }

        return length.ToString("X4", CultureInfo.InvariantCulture) + request;
    }

    private async Task<(TcpClient Client, NetworkStream Stream)> ConnectAsync(
        AdbEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            var stream = await ConnectStreamAsync(client, endpoint, cancellationToken).ConfigureAwait(false);
            return (client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<NetworkStream> ConnectStreamAsync(
        TcpClient client,
        AdbEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_connectTimeout);
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The adb server at {endpoint} did not accept a connection within {_connectTimeout.TotalSeconds:0.#} seconds.");
        }

        return client.GetStream();
    }

    private static async Task SendRequestAsync(
        NetworkStream stream,
        string request,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(FormatRequest(request));
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReadOkayAsync(NetworkStream stream, string request, CancellationToken cancellationToken)
    {
        var status = new byte[StatusLength];
        try
        {
            await stream.ReadExactlyAsync(status, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new AdbProtocolException($"The adb server closed the connection after '{request}'.");
        }

        var text = Encoding.ASCII.GetString(status);
        if (string.Equals(text, "OKAY", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(text, "FAIL", StringComparison.Ordinal))
        {
            var message = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
            throw new AdbProtocolException(
                string.IsNullOrWhiteSpace(message)
                    ? $"The adb server rejected '{request}'."
                    : $"The adb server rejected '{request}': {message}");
        }

        throw new AdbProtocolException($"The adb server returned an unexpected status '{text}' for '{request}'.");
    }

    private static async Task<string?> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[StatusLength];
        try
        {
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var lengthText = Encoding.ASCII.GetString(header);
        if (!int.TryParse(lengthText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length) ||
            length < 0 ||
            length > MaxFrameLength)
        {
            throw new AdbProtocolException($"The adb server sent an invalid frame length '{lengthText}'.");
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new AdbProtocolException("The adb server closed the connection in the middle of a frame.");
        }

        return Encoding.UTF8.GetString(payload);
    }

    private sealed class AdbTrackSession : IAdbTrackSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private int _disposed;

        public AdbTrackSession(TcpClient client, NetworkStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public async Task<string?> ReadFrameAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await AdbSocketClient.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _stream.Dispose();
                _client.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
