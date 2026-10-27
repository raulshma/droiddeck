using DroidDeck.Core.Devices;

namespace DroidDeck.Adb.Tests;

public sealed class DeviceInfoTests
{
    [Theory]
    [InlineData("R58M123ABC", false, DeviceConnectionKind.Usb)]
    [InlineData("192.168.1.10:5555", false, DeviceConnectionKind.Wireless)]
    [InlineData("adb-R58M123ABC-xyz._adb-tls-connect._tcp", false, DeviceConnectionKind.Wireless)]
    [InlineData("emulator-5554", true, DeviceConnectionKind.Emulator)]
    [InlineData("0123456789ABCDEF", true, DeviceConnectionKind.Emulator)]
    public void ConnectionKind_IsDerivedFromSerialAndEmulatorFlag(
        string serial,
        bool isEmulator,
        DeviceConnectionKind expected)
    {
        var device = new DeviceInfo { Serial = serial, IsEmulator = isEmulator };

        Assert.Equal(expected, device.ConnectionKind);
    }
}
