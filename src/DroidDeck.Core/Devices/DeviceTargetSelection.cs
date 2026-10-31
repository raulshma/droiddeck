namespace DroidDeck.Core.Devices;

/// <summary>
/// The ready devices for a page and the device chosen as the command target.
/// </summary>
/// <param name="Usable">The devices that are ready for commands, in input order.</param>
/// <param name="Selected">The chosen device, or <see langword="null"/> when no device is usable.</param>
public sealed record DeviceTargetSelection(IReadOnlyList<DeviceInfo> Usable, DeviceInfo? Selected)
{
    /// <summary>
    /// Filters a device list to ready devices and chooses a target from them.
    /// </summary>
    /// <param name="devices">The device list to inspect. The input is not modified.</param>
    /// <param name="previousSerial">The serial selected before the refresh, if any.</param>
    /// <param name="workspaceSerial">The serial remembered by the workspace, if any.</param>
    /// <returns>
    /// The ready devices in input order and the selected device, or an empty list and
    /// <see langword="null"/> when no device is ready.
    /// </returns>
    /// <remarks>
    /// The first ready device matching <paramref name="previousSerial"/> wins, then the
    /// first matching <paramref name="workspaceSerial"/>, then the first ready device.
    /// Serial comparisons are ordinal.
    /// </remarks>
    public static DeviceTargetSelection Resolve(
        IEnumerable<DeviceInfo> devices,
        string? previousSerial,
        string? workspaceSerial)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var usable = devices.Where(device => device.CanRunActions).ToArray();
        var selected = FindBySerial(usable, previousSerial)
                       ?? FindBySerial(usable, workspaceSerial)
                       ?? usable.FirstOrDefault();

        return new DeviceTargetSelection(usable, selected);
    }

    private static DeviceInfo? FindBySerial(IReadOnlyList<DeviceInfo> devices, string? serial) =>
        string.IsNullOrWhiteSpace(serial)
            ? null
            : devices.FirstOrDefault(device =>
                string.Equals(device.Serial, serial, StringComparison.Ordinal));
}
