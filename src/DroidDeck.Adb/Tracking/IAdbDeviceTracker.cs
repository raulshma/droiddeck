using DroidDeck.Core.Devices;

namespace DroidDeck.Adb.Tracking;

/// <summary>Describes a device-list change.</summary>
public sealed class AdbDevicesChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for a device-list change.</summary>
    public AdbDevicesChangedEventArgs(IReadOnlyList<DeviceInfo> devices) => Devices = devices;

    /// <summary>Gets the current device snapshot.</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; }
}

/// <summary>Describes a transport connection change.</summary>
public sealed class AdbConnectionChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for a connection change.</summary>
    public AdbConnectionChangedEventArgs(bool isConnected, bool isUsingProcessFallback, string? error)
    {
        IsConnected = isConnected;
        IsUsingProcessFallback = isUsingProcessFallback;
        Error = error;
    }

    /// <summary>Gets whether device data is available.</summary>
    public bool IsConnected { get; }

    /// <summary>Gets whether the CLI fallback is supplying device data.</summary>
    public bool IsUsingProcessFallback { get; }

    /// <summary>Gets the last transport error, if any.</summary>
    public string? Error { get; }
}

/// <summary>Tracks connected devices through the adb server.</summary>
public interface IAdbDeviceTracker : IAsyncDisposable
{
    /// <summary>Gets the current device snapshot.</summary>
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>Gets whether device data is currently available.</summary>
    bool IsConnected { get; }

    /// <summary>Gets whether the CLI fallback is supplying device data.</summary>
    bool IsUsingProcessFallback { get; }

    /// <summary>Gets the last transport error, if any.</summary>
    string? LastError { get; }

    /// <summary>Starts tracking.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops tracking.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches the device list immediately.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Kills and restarts the adb server.</summary>
    Task<bool> RestartServerAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when devices are added, removed, or change state.</summary>
    event EventHandler<AdbDevicesChangedEventArgs>? DevicesChanged;

    /// <summary>Raised when the transport connects, disconnects, or falls back.</summary>
    event EventHandler<AdbConnectionChangedEventArgs>? ConnectionChanged;
}
