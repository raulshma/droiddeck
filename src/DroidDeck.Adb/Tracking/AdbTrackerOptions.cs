namespace DroidDeck.Adb.Tracking;

/// <summary>Configures device tracking.</summary>
public sealed record AdbTrackerOptions
{
    /// <summary>Gets the adb server endpoint.</summary>
    public AdbEndpoint Endpoint { get; init; } = AdbEndpoint.Default;

    /// <summary>Gets the adb executable used for bootstrap and fallback commands.</summary>
    public string AdbExecutable { get; init; } = "adb";

    /// <summary>Gets the hotplug debounce window.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Gets how long a socket connection may take.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the reconnect backoff sequence; the last value repeats.</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];

    /// <summary>Gets whether <c>adb devices -l</c> polling supplies data while the socket is unavailable.</summary>
    public bool EnableProcessFallback { get; init; } = true;

    /// <summary>Gets the CLI fallback poll interval.</summary>
    public TimeSpan ProcessPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Gets the timeout for bootstrap and fallback commands.</summary>
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
