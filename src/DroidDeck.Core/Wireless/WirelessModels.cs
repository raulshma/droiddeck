namespace DroidDeck.Core.Wireless;

/// <summary>The kind of mDNS service advertised by an Android device.</summary>
public enum WirelessServiceKind
{
    /// <summary>A temporary pairing service.</summary>
    Pairing,

    /// <summary>A persistent adb-over-Wi-Fi connection service.</summary>
    Connect,
}

/// <summary>A wireless adb service discovered through mDNS.</summary>
public sealed record WirelessMdnsService
{
    /// <summary>Gets the advertised instance name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the service kind.</summary>
    public WirelessServiceKind Kind { get; init; }

    /// <summary>Gets the host name, address, or IP reported by mDNS.</summary>
    public required string Host { get; init; }

    /// <summary>Gets the service port.</summary>
    public int Port { get; init; }

    /// <summary>Gets whether an adb transport is already connected to this service.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Gets the <c>host:port</c> address accepted by <c>adb pair</c> and <c>adb connect</c>.</summary>
    public string Address => Port > 0 ? $"{Host}:{Port}" : Host;

    /// <summary>Gets a short state label for the UI.</summary>
    public string StateLabel => Kind == WirelessServiceKind.Pairing
        ? "Ready to pair"
        : IsConnected ? "Connected" : "Available";
}

/// <summary>A host-generated QR pairing session.</summary>
public sealed record WirelessPairingSession
{
    /// <summary>Gets the requested mDNS pairing service instance name.</summary>
    public required string ServiceName { get; init; }

    /// <summary>Gets the pairing code that is also encoded in the QR payload.</summary>
    public required string PairingCode { get; init; }

    /// <summary>Gets the WPA3/ADB QR payload.</summary>
    public required string QrPayload { get; init; }

    /// <summary>Gets when the session stops accepting a scan.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Gets a PNG image of the QR payload.</summary>
    public byte[]? QrPng { get; init; }
}

/// <summary>A wireless device remembered for automatic reconnect.</summary>
public sealed record WirelessDevice
{
    /// <summary>Gets the <c>host:port</c> address.</summary>
    public required string Address { get; init; }

    /// <summary>Gets a friendly name captured from mDNS or the device model.</summary>
    public string? Name { get; init; }

    /// <summary>Gets when the device last connected.</summary>
    public DateTimeOffset? LastConnectedAt { get; init; }

    /// <summary>Gets whether automatic reconnect is enabled for this device.</summary>
    public bool AutoReconnect { get; init; } = true;
}

/// <summary>The outcome of a wireless adb operation.</summary>
public sealed record WirelessOperationResult
{
    /// <summary>Gets whether the operation succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets a user-facing summary.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the exact adb command that ran, when one ran.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Gets specific, actionable guidance for a common failure.</summary>
    public string? Guidance { get; init; }

    /// <summary>Gets the connected device address when the operation established a transport.</summary>
    public string? Address { get; init; }
}

/// <summary>Persists wireless devices for reconnect after adb or network restarts.</summary>
public interface IWirelessDeviceStore
{
    /// <summary>Lists remembered devices.</summary>
    Task<IReadOnlyList<WirelessDevice>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a remembered device.</summary>
    Task SaveAsync(WirelessDevice device, CancellationToken cancellationToken = default);

    /// <summary>Removes a remembered device.</summary>
    Task RemoveAsync(string address, CancellationToken cancellationToken = default);
}

/// <summary>Options for the automatic reconnect loop.</summary>
/// <param name="Enabled">Whether remembered devices reconnect automatically.</param>
/// <param name="Delay">The pause between reconnect attempts.</param>
public sealed record WirelessReconnectOptions(bool Enabled = true, TimeSpan? Delay = null)
{
    /// <summary>Gets the effective reconnect delay.</summary>
    public TimeSpan EffectiveDelay => Delay ?? TimeSpan.FromSeconds(10);
}

/// <summary>Discovers, pairs, connects, and reconnects wireless adb devices.</summary>
public interface IWirelessAdbService : IAsyncDisposable
{
    /// <summary>Gets the latest mDNS discovery snapshot.</summary>
    IReadOnlyList<WirelessMdnsService> DiscoveredServices { get; }

    /// <summary>Gets remembered wireless devices.</summary>
    IReadOnlyList<WirelessDevice> RememberedDevices { get; }

    /// <summary>Gets whether the automatic reconnect loop is running.</summary>
    bool IsReconnecting { get; }

    /// <summary>Gets the last discovery or reconnect error, if any.</summary>
    string? LastError { get; }

    /// <summary>Raised when the mDNS or remembered-device set changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Starts automatic reconnect of remembered devices.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops automatic reconnect.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes mDNS services through <c>adb mdns services</c>.</summary>
    Task<IReadOnlyList<WirelessMdnsService>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a host-generated QR pairing session.</summary>
    WirelessPairingSession CreatePairingSession(TimeSpan? lifetime = null);

    /// <summary>
    /// Waits for the phone that scanned a QR session to advertise its pairing service,
    /// then pairs and connects.
    /// </summary>
    Task<WirelessOperationResult> CompleteQrPairingAsync(
        WirelessPairingSession session,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Pairs with a device using a pairing service address and code.</summary>
    Task<WirelessOperationResult> PairAsync(
        string address,
        string pairingCode,
        bool remember = true,
        CancellationToken cancellationToken = default);

    /// <summary>Connects to a paired device.</summary>
    Task<WirelessOperationResult> ConnectAsync(
        string address,
        string? name = null,
        bool remember = true,
        CancellationToken cancellationToken = default);

    /// <summary>Disconnects from a wireless device, optionally forgetting it.</summary>
    Task<WirelessOperationResult> DisconnectAsync(
        string address,
        bool forget = false,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to reconnect every remembered device that is not currently connected.</summary>
    Task<IReadOnlyList<WirelessOperationResult>> ReconnectAsync(CancellationToken cancellationToken = default);
}
