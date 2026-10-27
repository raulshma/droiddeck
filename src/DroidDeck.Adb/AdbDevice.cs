using DroidDeck.Core.Devices;

namespace DroidDeck.Adb;

/// <summary>A device as reported by the adb server.</summary>
public sealed record AdbDevice
{
    /// <summary>Gets the adb serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets the transport state.</summary>
    public DeviceState State { get; init; } = DeviceState.Unknown;

    /// <summary>Gets the product name reported by the device.</summary>
    public string? Product { get; init; }

    /// <summary>Gets the model name reported by the device.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the device codename.</summary>
    public string? Device { get; init; }

    /// <summary>Gets the adb transport identifier.</summary>
    public string? TransportId { get; init; }

    /// <summary>Gets whether the device is an emulator.</summary>
    public bool IsEmulator { get; init; }

    /// <summary>Gets the display name, preferring the model.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Model)
        ? Model
        : !string.IsNullOrWhiteSpace(Device)
            ? Device
            : Serial;
}
