namespace DroidDeck.Adb.Protocol;

/// <summary>A persistent socket connection that receives device-list frames.</summary>
public interface IAdbTrackSession : IAsyncDisposable
{
    /// <summary>
    /// Reads the next device-list frame. Returns <see langword="null"/> when the server
    /// closes the connection.
    /// </summary>
    Task<string?> ReadFrameAsync(CancellationToken cancellationToken = default);
}

/// <summary>Speaks the adb server wire protocol over TCP.</summary>
public interface IAdbSocketClient
{
    /// <summary>Opens a long-lived <c>host:track-devices-l</c> session.</summary>
    Task<IAdbTrackSession> TrackDevicesAsync(AdbEndpoint endpoint, CancellationToken cancellationToken = default);

    /// <summary>Sends a single host request and reads its payload.</summary>
    Task<string> SendAsync(AdbEndpoint endpoint, string request, CancellationToken cancellationToken = default);
}
