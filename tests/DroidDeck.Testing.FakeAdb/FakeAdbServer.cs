using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DroidDeck.Testing.FakeAdb;

internal static class FakeAdbServer
{
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<int> RunAsync(FakeAdbOptions options)
    {
        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine($"fake adb: cannot listen on port {options.Port}: {exception.Message}");
            return 1;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        FakeAdbState.WritePort(options, port);
        TryWriteReadyLine(port);

        using var shutdown = new CancellationTokenSource();
        var clients = new List<Task>();
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                clients.Add(Task.Run(() => HandleClientAsync(client, options, shutdown), CancellationToken.None));
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(clients).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task HandleClientAsync(
        TcpClient client,
        FakeAdbOptions options,
        CancellationTokenSource shutdown)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, shutdown.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                switch (request)
                {
                    case "host:track-devices-l" when options.Scenario == "socket-failure":
                        await WriteStatusAsync(stream, "FAIL", shutdown.Token).ConfigureAwait(false);
                        await WriteFrameAsync(stream, "mock socket failure", shutdown.Token).ConfigureAwait(false);
                        break;
                    case "host:track-devices-l":
                        await WriteStatusAsync(stream, "OKAY", shutdown.Token).ConfigureAwait(false);
                        await StreamDevicesAsync(stream, options, shutdown.Token).ConfigureAwait(false);
                        break;
                    case "host:devices-l":
                        await WriteStatusAsync(stream, "OKAY", shutdown.Token).ConfigureAwait(false);
                        await WriteFrameAsync(
                                stream,
                                FakeAdbState.BuildPayload(FakeAdbState.ReadDeviceLines(options)),
                                shutdown.Token)
                            .ConfigureAwait(false);
                        break;
                    case "host:version":
                        await WriteStatusAsync(stream, "OKAY", shutdown.Token).ConfigureAwait(false);
                        await WriteFrameAsync(stream, "0029", shutdown.Token).ConfigureAwait(false);
                        break;
                    case "host:kill":
                        await WriteStatusAsync(stream, "OKAY", shutdown.Token).ConfigureAwait(false);
                        await shutdown.CancelAsync().ConfigureAwait(false);
                        break;
                    default:
                        await WriteStatusAsync(stream, "FAIL", shutdown.Token).ConfigureAwait(false);
                        await WriteFrameAsync(
                                stream,
                                $"fake adb does not support '{request}'",
                                shutdown.Token)
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task StreamDevicesAsync(
        NetworkStream stream,
        FakeAdbOptions options,
        CancellationToken cancellationToken)
    {
        string? previous = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = FakeAdbState.BuildPayload(FakeAdbState.ReadDeviceLines(options));
            if (!string.Equals(payload, previous, StringComparison.Ordinal))
            {
                await WriteFrameAsync(stream, payload, cancellationToken).ConfigureAwait(false);
                previous = payload;
            }

            await Task.Delay(DevicePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
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
            length is < 0 or > 65535)
        {
            return null;
        }

        if (length == 0)
        {
            return string.Empty;
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload);
    }

    private static Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(status), cancellationToken).AsTask();

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var header = Encoding.ASCII.GetBytes(
            bytes.Length.ToString("X4", CultureInfo.InvariantCulture));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > 0)
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TryWriteReadyLine(int port)
    {
        try
        {
            Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"FAKEADB_READY {port}"));
            Console.Out.Flush();
        }
        catch (IOException)
        {
        }
    }
}
