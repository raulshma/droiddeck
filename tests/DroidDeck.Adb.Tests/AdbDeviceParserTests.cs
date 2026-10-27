using DroidDeck.Core.Devices;

namespace DroidDeck.Adb.Tests;

public sealed class AdbDeviceParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("List of devices attached\n\n")]
    [InlineData("* daemon not running; starting now at tcp:5037\n* daemon started successfully\n")]
    public void Parse_IgnoresEmptyAndPreamblePayloads(string? payload) =>
        Assert.Empty(AdbDeviceParser.Parse(payload));

    [Fact]
    public void Parse_ReadsLongFormatFields()
    {
        var device = Assert.Single(AdbDeviceParser.Parse(
            "R58M123ABC device product:beyond1lte model:SM_G973F device:beyond1 transport_id:3\n"));

        Assert.Equal("R58M123ABC", device.Serial);
        Assert.Equal(DeviceState.Device, device.State);
        Assert.Equal("beyond1lte", device.Product);
        Assert.Equal("SM G973F", device.Model);
        Assert.Equal("beyond1", device.Device);
        Assert.Equal("3", device.TransportId);
        Assert.False(device.IsEmulator);
        Assert.Equal("SM G973F", device.DisplayName);
    }

    [Fact]
    public void Parse_DetectsEmulatorFromSerialAndProduct()
    {
        var devices = AdbDeviceParser.Parse(
            "emulator-5554 device product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64xa transport_id:1\n");

        var device = Assert.Single(devices);
        Assert.True(device.IsEmulator);
        Assert.Equal("sdk gphone64 x86 64", device.Model);
    }

    [Fact]
    public void Parse_DetectsEmulatorFromDeviceCodename()
    {
        var device = Assert.Single(AdbDeviceParser.Parse(
            "192.168.1.10:5555 device product:generic model:Android_SDK_built_for_x86 device:generic_x86\n"));

        Assert.True(device.IsEmulator);
    }

    [Theory]
    [InlineData("device", DeviceState.Device)]
    [InlineData("offline", DeviceState.Offline)]
    [InlineData("unauthorized", DeviceState.Unauthorized)]
    [InlineData("authorizing", DeviceState.Authorizing)]
    [InlineData("recovery", DeviceState.Recovery)]
    [InlineData("sideload", DeviceState.Sideload)]
    [InlineData("bootloader", DeviceState.Bootloader)]
    [InlineData("connecting", DeviceState.Connecting)]
    [InlineData("no permissions (user in plugdev group)", DeviceState.Unknown)]
    public void Parse_MapsStates(string stateText, DeviceState expected)
    {
        var device = Assert.Single(AdbDeviceParser.Parse($"ABC123 {stateText}\n"));
        Assert.Equal(expected, device.State);
    }

    [Fact]
    public void Parse_SortsBySerial()
    {
        var devices = AdbDeviceParser.Parse(
            "zeta device model:Z\n" +
            "alpha device model:A\n" +
            "EMULATOR-5556 device model:E\n" +
            "emulator-5554 device model:E2\n");

        Assert.Equal(
            ["alpha", "emulator-5554", "EMULATOR-5556", "zeta"],
            devices.Select(static device => device.Serial));
    }

    [Fact]
    public void Parse_HandlesWindowsLineEndingsAndBlankLines()
    {
        var devices = AdbDeviceParser.Parse("one device\r\n\r\ntwo unauthorized\r\n");
        Assert.Equal(2, devices.Count);
        Assert.Equal(DeviceState.Unauthorized, devices[1].State);
    }

    [Fact]
    public void ParseLine_ReturnsNullForDaemonErrors()
    {
        Assert.Null(AdbDeviceParser.ParseLine("error: cannot connect to daemon"));
        Assert.Null(AdbDeviceParser.ParseLine("adb server version (41) does not match this client (39)"));
        Assert.Null(AdbDeviceParser.ParseLine("    "));
    }

    [Fact]
    public void Parse_ShowsSerialWhenModelIsMissing()
    {
        var device = Assert.Single(AdbDeviceParser.Parse("0123456789ABCDEF device\n"));
        Assert.Null(device.Model);
        Assert.Equal("0123456789ABCDEF", device.DisplayName);
    }
}
