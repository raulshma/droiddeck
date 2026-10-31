namespace DroidDeck.Core.Devices;

/// <summary>How a device is connected to this computer.</summary>
public enum DeviceConnectionKind
{
    /// <summary>The connection kind is not known.</summary>
    Unknown,

    /// <summary>The device is connected over USB.</summary>
    Usb,

    /// <summary>The device is connected over Wi-Fi.</summary>
    Wireless,

    /// <summary>The device is an emulator.</summary>
    Emulator,
}

/// <summary>The connection state reported by adb.</summary>
public enum DeviceState
{
    /// <summary>The state is not recognized.</summary>
    Unknown,

    /// <summary>A wireless connection is being established.</summary>
    Connecting,

    /// <summary>The device is waiting for the user to authorize this computer.</summary>
    Authorizing,

    /// <summary>The device has not authorized this computer.</summary>
    Unauthorized,

    /// <summary>The device is ready.</summary>
    Device,

    /// <summary>The device is connected but not responding.</summary>
    Offline,

    /// <summary>The device is in recovery.</summary>
    Recovery,

    /// <summary>The device is in sideload mode.</summary>
    Sideload,

    /// <summary>The device is in the bootloader.</summary>
    Bootloader,

    /// <summary>The device was disconnected.</summary>
    Disconnected,
}

/// <summary>
/// A connected device with transport and metadata information.
/// </summary>
public sealed record DeviceInfo
{
    /// <summary>Gets the adb serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets the connection state.</summary>
    public DeviceState State { get; init; } = DeviceState.Unknown;

    /// <summary>Gets the manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Gets the model.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the product name.</summary>
    public string? Product { get; init; }

    /// <summary>Gets the device codename reported by adb.</summary>
    public string? Codename { get; init; }

    /// <summary>Gets the adb transport identifier.</summary>
    public string? TransportId { get; init; }

    /// <summary>Gets the Android release version.</summary>
    public string? AndroidVersion { get; init; }

    /// <summary>Gets the API level.</summary>
    public int? ApiLevel { get; init; }

    /// <summary>Gets the primary ABI.</summary>
    public string? Abi { get; init; }

    /// <summary>Gets all supported ABIs.</summary>
    public IReadOnlyList<string> AbiList { get; init; } = [];

    /// <summary>Gets whether the device is an emulator.</summary>
    public bool IsEmulator { get; init; }

    /// <summary>Gets the emulator AVD name.</summary>
    public string? AvdName { get; init; }

    /// <summary>Gets the screen size as <c>widthxheight</c>.</summary>
    public string? ScreenSize { get; init; }

    /// <summary>Gets the screen density in dpi.</summary>
    public int? Density { get; init; }

    /// <summary>Gets the battery level percentage.</summary>
    public int? BatteryLevel { get; init; }

    /// <summary>Gets the battery status label.</summary>
    public string? BatteryStatus { get; init; }

    /// <summary>Gets the Wi-Fi IP address.</summary>
    public string? IpAddress { get; init; }

    /// <summary>Gets the build type.</summary>
    public string? BuildType { get; init; }

    /// <summary>Gets whether the build is debuggable.</summary>
    public bool? IsDebuggable { get; init; }

    /// <summary>Gets the device characteristics (tv, watch, automotive).</summary>
    public string? Characteristics { get; init; }

    /// <summary>Gets user-defined tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Gets user notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Gets the total size of the data partition in bytes.</summary>
    public long? StorageTotalBytes { get; init; }

    /// <summary>Gets the free space on the data partition in bytes.</summary>
    public long? StorageFreeBytes { get; init; }

    /// <summary>Gets when the device was last seen.</summary>
    public DateTimeOffset LastSeenAt { get; init; } = DateTimeOffset.MinValue;

    /// <summary>Gets when metadata was last refreshed.</summary>
    public DateTimeOffset? MetadataUpdatedAt { get; init; }

    /// <summary>Gets the display name, preferring the model.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ? Serial : Model;

    /// <summary>Gets how the device is connected.</summary>
    public DeviceConnectionKind ConnectionKind =>
        IsEmulator
            ? DeviceConnectionKind.Emulator
            : Serial.Contains(':', StringComparison.Ordinal) ||
              Serial.Contains("._adb-tls-", StringComparison.OrdinalIgnoreCase)
                ? DeviceConnectionKind.Wireless
                : DeviceConnectionKind.Usb;

    /// <summary>Gets whether mutating actions are allowed.</summary>
    public bool CanRunActions => State == DeviceState.Device;

    /// <summary>
    /// Applies a transport snapshot from a device-list update, preserving metadata that
    /// adb does not report and recording when the transport was seen.
    /// </summary>
    /// <param name="transport">The device parsed from <c>adb devices -l</c>.</param>
    /// <param name="seenAt">When the transport snapshot was observed.</param>
    public DeviceInfo WithTransport(DeviceInfo transport, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return this with
        {
            State = transport.State,
            Product = transport.Product ?? Product,
            Codename = transport.Codename ?? Codename,
            Model = transport.Model ?? Model,
            TransportId = transport.TransportId ?? TransportId,
            IsEmulator = transport.IsEmulator || IsEmulator,
            LastSeenAt = seenAt,
        };
    }

    /// <summary>Merges freshly fetched metadata into this device.</summary>
    public DeviceInfo WithMetadata(DeviceMetadata metadata, DateTimeOffset appliedAt) => this with
    {
        Manufacturer = metadata.Manufacturer ?? Manufacturer,
        Model = metadata.Model ?? Model,
        Product = metadata.Product ?? Product,
        AndroidVersion = metadata.AndroidVersion ?? AndroidVersion,
        ApiLevel = metadata.ApiLevel ?? ApiLevel,
        Abi = metadata.Abi ?? Abi,
        AbiList = metadata.AbiList.Count > 0 ? metadata.AbiList : AbiList,
        IsEmulator = metadata.IsEmulator ?? IsEmulator,
        AvdName = metadata.AvdName ?? AvdName,
        ScreenSize = metadata.ScreenSize ?? ScreenSize,
        Density = metadata.Density ?? Density,
        BatteryLevel = metadata.BatteryLevel ?? BatteryLevel,
        BatteryStatus = metadata.BatteryStatus ?? BatteryStatus,
        IpAddress = metadata.IpAddress ?? IpAddress,
        BuildType = metadata.BuildType ?? BuildType,
        IsDebuggable = metadata.IsDebuggable ?? IsDebuggable,
        Characteristics = metadata.Characteristics ?? Characteristics,
        StorageTotalBytes = metadata.StorageTotalBytes ?? StorageTotalBytes,
        StorageFreeBytes = metadata.StorageFreeBytes ?? StorageFreeBytes,
        MetadataUpdatedAt = appliedAt,
    };

    /// <summary>Applies a persisted device record.</summary>
    public DeviceInfo WithRecord(DeviceRecord record) => this with
    {
        Notes = record.Notes,
        Tags = record.Tags,
    };
}

/// <summary>
/// Metadata fetched in a single batched shell request. Missing fields indicate a partial failure.
/// </summary>
public sealed record DeviceMetadata
{
    /// <summary>Gets the manufacturer.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Gets the model.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the product name.</summary>
    public string? Product { get; init; }

    /// <summary>Gets the Android release version.</summary>
    public string? AndroidVersion { get; init; }

    /// <summary>Gets the API level.</summary>
    public int? ApiLevel { get; init; }

    /// <summary>Gets the primary ABI.</summary>
    public string? Abi { get; init; }

    /// <summary>Gets all supported ABIs.</summary>
    public IReadOnlyList<string> AbiList { get; init; } = [];

    /// <summary>Gets whether the device is an emulator.</summary>
    public bool? IsEmulator { get; init; }

    /// <summary>Gets the emulator AVD name.</summary>
    public string? AvdName { get; init; }

    /// <summary>Gets the screen size as <c>widthxheight</c>.</summary>
    public string? ScreenSize { get; init; }

    /// <summary>Gets the density in dpi.</summary>
    public int? Density { get; init; }

    /// <summary>Gets the battery level percentage.</summary>
    public int? BatteryLevel { get; init; }

    /// <summary>Gets the battery status label.</summary>
    public string? BatteryStatus { get; init; }

    /// <summary>Gets the Wi-Fi IP address.</summary>
    public string? IpAddress { get; init; }

    /// <summary>Gets the build type.</summary>
    public string? BuildType { get; init; }

    /// <summary>Gets whether the build is debuggable.</summary>
    public bool? IsDebuggable { get; init; }

    /// <summary>Gets the device characteristics.</summary>
    public string? Characteristics { get; init; }

    /// <summary>Gets the total size of the data partition in bytes.</summary>
    public long? StorageTotalBytes { get; init; }

    /// <summary>Gets the free space on the data partition in bytes.</summary>
    public long? StorageFreeBytes { get; init; }

    /// <summary>Gets warnings for fields that could not be read.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Gets when the metadata was fetched.</summary>
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.MinValue;
}

/// <summary>
/// The persisted, user-editable part of a device record.
/// </summary>
public sealed record DeviceRecord
{
    /// <summary>Gets the adb serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets user notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Gets user tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>Guidance for device states.</summary>
public static class DeviceStateGuidance
{
    /// <summary>Gets a short explanation and next step for a state.</summary>
    public static string For(DeviceState state) => state switch
    {
        DeviceState.Device => "Ready.",
        DeviceState.Unauthorized =>
            "Allow USB debugging on the device. Unlock the phone, tap \"Allow\" on the RSA prompt, then retry.",
        DeviceState.Authorizing =>
            "Waiting for authorization. Unlock the device and accept the USB debugging prompt.",
        DeviceState.Offline =>
            "The device is not responding. Reconnect the cable or use \"Restart adb\" from the Devices page.",
        DeviceState.Connecting => "Connecting over Wi-Fi. Keep the device on the same network.",
        DeviceState.Recovery => "The device is in recovery mode. Limited actions are available.",
        DeviceState.Sideload => "The device is in sideload mode. Limited actions are available.",
        DeviceState.Bootloader => "The device is in the bootloader. Limited actions are available.",
        DeviceState.Disconnected => "The device was disconnected.",
        _ => "The device state is unknown. Reconnect the device and refresh.",
    };
}
