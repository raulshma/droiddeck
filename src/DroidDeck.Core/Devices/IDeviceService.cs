namespace DroidDeck.Core.Devices;

/// <summary>Describes a device-list change.</summary>
public sealed class DeviceListChangedEventArgs : EventArgs
{
    /// <summary>Creates event data for a device-list change.</summary>
    public DeviceListChangedEventArgs(IReadOnlyList<DeviceInfo> devices) => Devices = devices;

    /// <summary>Gets the current device snapshot.</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; }
}

/// <summary>
/// Tracks devices and exposes their metadata and user-editable records.
/// </summary>
public interface IDeviceService : IAsyncDisposable
{
    /// <summary>Gets the current device snapshot.</summary>
    IReadOnlyList<DeviceInfo> Devices { get; }

    /// <summary>Gets whether the adb tracker is connected.</summary>
    bool IsAdbConnected { get; }

    /// <summary>Gets whether device data is supplied by the CLI fallback.</summary>
    bool IsUsingCliFallback { get; }

    /// <summary>Gets the last tracker error, if any.</summary>
    string? LastError { get; }

    /// <summary>Starts device tracking.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops device tracking.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Refreshes the device list and metadata for connected devices.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches metadata for one device.</summary>
    Task<DeviceMetadata> RefreshMetadataAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Gets a device by serial.</summary>
    Task<DeviceInfo?> GetDeviceAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Persists notes for a device.</summary>
    Task SetNotesAsync(string serial, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Persists tags for a device.</summary>
    Task SetTagsAsync(string serial, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>Restarts the adb server and reconnects.</summary>
    Task<bool> RestartAdbAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised when devices are added, removed, or change state.</summary>
    event EventHandler<DeviceListChangedEventArgs>? DevicesChanged;

    /// <summary>Raised when a single device's metadata or record changes.</summary>
    event EventHandler<DeviceInfo>? DeviceUpdated;
}
