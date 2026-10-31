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

    [Fact]
    public void WithTransport_UpdatesTransportFieldsAndPreservesMetadata()
    {
        var seenAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var existing = new DeviceInfo
        {
            Serial = "S1",
            Manufacturer = "Google",
            BatteryLevel = 75,
            LastSeenAt = DateTimeOffset.MinValue,
        };
        var transport = new DeviceInfo
        {
            Serial = "S1",
            State = DeviceState.Device,
            Product = "panther",
            Codename = "panther",
            Model = "Pixel 7",
            TransportId = "4",
        };

        var merged = existing.WithTransport(transport, seenAt);

        Assert.Equal(DeviceState.Device, merged.State);
        Assert.Equal("panther", merged.Product);
        Assert.Equal("panther", merged.Codename);
        Assert.Equal("Pixel 7", merged.Model);
        Assert.Equal("4", merged.TransportId);
        Assert.Equal(seenAt, merged.LastSeenAt);
        Assert.Equal("Google", merged.Manufacturer);
        Assert.Equal(75, merged.BatteryLevel);
    }

    [Fact]
    public void WithTransport_KeepsExistingValuesWhenTransportOmitsThem()
    {
        var existing = new DeviceInfo
        {
            Serial = "S1",
            Product = "panther",
            Codename = "panther",
            Model = "Pixel 7",
            TransportId = "4",
            IsEmulator = true,
        };

        var merged = existing.WithTransport(
            new DeviceInfo { Serial = "S1", State = DeviceState.Offline },
            DateTimeOffset.UtcNow);

        Assert.Equal(DeviceState.Offline, merged.State);
        Assert.Equal("panther", merged.Product);
        Assert.Equal("panther", merged.Codename);
        Assert.Equal("Pixel 7", merged.Model);
        Assert.Equal("4", merged.TransportId);
        Assert.True(merged.IsEmulator);
    }
}
