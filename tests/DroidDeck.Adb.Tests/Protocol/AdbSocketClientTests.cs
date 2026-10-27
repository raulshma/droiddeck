using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DroidDeck.Adb.Protocol;
using DroidDeck.Core.Devices;
using DroidDeck.TestSupport;

namespace DroidDeck.Adb.Tests.Protocol;

public sealed class AdbSocketClientTests
{
    [Fact]
    public void FormatRequest_UsesFourDigitHexLength()
    {
        Assert.Equal("000Chost:version", AdbSocketClient.FormatRequest("host:version"));
        Assert.Equal("0014host:track-devices-l", AdbSocketClient.FormatRequest("host:track-devices-l"));
    }

    [Fact]
    public async Task SendAsync_ReadsVersionPayload()
    {
        await using var server = new FakeAdbServer();
        var client = new AdbSocketClient(TimeSpan.FromSeconds(2));

        var version = await client.SendAsync(server.Endpoint, "host:version");

        Assert.Equal("0029", version);
    }

    [Fact]
    public async Task SendAsync_ReadsDeviceList()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync("S1 device product:panther model:Pixel_7 device:panther transport_id:1");
        var client = new AdbSocketClient(TimeSpan.FromSeconds(2));

        var payload = await client.SendAsync(server.Endpoint, "host:devices-l");
        var device = Assert.Single(AdbDeviceParser.Parse(payload));

        Assert.Equal("S1", device.Serial);
        Assert.Equal("Pixel 7", device.Model);
    }

    [Fact]
    public async Task SendAsync_ThrowsProtocolExceptionOnFailureStatus()
    {
        await using var server = new FakeAdbServer { FailureMessage = "device offline" };
        var client = new AdbSocketClient(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<AdbProtocolException>(
            () => client.SendAsync(server.Endpoint, "host:devices-l"));

        Assert.Contains("device offline", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrackDevicesAsync_ReceivesInitialAndUpdatedFrames()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync("S1 device model:One");
        var client = new AdbSocketClient(TimeSpan.FromSeconds(2));

        await using var session = await client.TrackDevicesAsync(server.Endpoint);

        var initial = await session.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("S1", initial, StringComparison.Ordinal);

        await server.SetDevicesAsync("S1 device model:One", "S2 unauthorized model:Two");

        var update = await session.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var devices = AdbDeviceParser.Parse(update);
        Assert.Equal(2, devices.Count);
        Assert.Equal(DeviceState.Unauthorized, devices.Single(static device => device.Serial == "S2").State);
    }

    [Fact]
    public async Task TrackSession_ReturnsNullWhenServerCloses()
    {
        await using var server = new FakeAdbServer();
        var client = new AdbSocketClient(TimeSpan.FromSeconds(2));
        await using var session = await client.TrackDevicesAsync(server.Endpoint);
        _ = await session.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));

        server.DropConnections();

        var frame = await session.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(frame);
    }

    [Fact]
    public async Task TrackSession_RejectsReadsAfterDispose()
    {
        await using var server = new FakeAdbServer();
        var client = new AdbSocketClient(TimeSpan.FromSeconds(2));
        var session = await client.TrackDevicesAsync(server.Endpoint);
        _ = await session.ReadFrameAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ReadFrameAsync());
    }

    [Fact]
    public async Task SendAsync_ReportsTransportFailureWhenServerIsDown()
    {
        var port = GetUnusedPort();
        var client = new AdbSocketClient(TimeSpan.FromSeconds(1));

        var exception = await Record.ExceptionAsync(
            () => client.SendAsync(new AdbEndpoint("127.0.0.1", port), "host:version"));

        Assert.True(
            exception is SocketException or TimeoutException,
            $"Expected a socket or timeout failure, but got {exception?.GetType().Name ?? "no exception"}.");
    }

    [Fact]
    public void FormatRequest_RejectsOversizedRequests()
    {
        var request = new string('a', 70_000);
        Assert.Throws<ArgumentException>(() => AdbSocketClient.FormatRequest(request));
    }

    private static int GetUnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
