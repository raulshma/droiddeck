using DroidDeck.Core.Devices;

namespace DroidDeck.Core.Tests.Devices;

public sealed class DeviceTargetSelectionTests
{
    private static readonly string[] DefaultSerials = ["device-a", "device-b", "device-c"];

    private static readonly string[] ReverseSerials = ["device-b", "device-a"];

    [Fact]
    public void Resolve_PrefersThePreviousSerial()
    {
        var devices = new[]
        {
            Device("device-a"),
            Device("device-b"),
            Device("device-c"),
        };

        var result = DeviceTargetSelection.Resolve(devices, "device-b", "device-c");

        Assert.Equal(DefaultSerials, result.Usable.Select(device => device.Serial));
        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_FallsBackToTheWorkspaceSerial()
    {
        var devices = new[]
        {
            Device("device-a"),
            Device("device-b"),
        };

        var result = DeviceTargetSelection.Resolve(devices, null, "device-b");

        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_FallsBackToTheFirstUsableDevice()
    {
        var devices = new[]
        {
            Device("device-a", DeviceState.Offline),
            Device("device-b"),
            Device("device-c"),
        };

        var result = DeviceTargetSelection.Resolve(devices, "missing", "also-missing");

        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_IgnoresDevicesThatAreNotReady()
    {
        var offline = Device("device-a", DeviceState.Offline);
        var ready = Device("device-b");
        var unauthorized = Device("device-c", DeviceState.Unauthorized);

        var result = DeviceTargetSelection.Resolve([offline, ready, unauthorized], "device-a", "device-c");

        Assert.Equal(new[] { ready }, result.Usable);
        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_ReturnsAnEmptyUsableListAndNullWhenThereAreNoDevices()
    {
        var result = DeviceTargetSelection.Resolve([], "device-a", "device-b");

        Assert.Empty(result.Usable);
        Assert.Null(result.Selected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_IgnoresNullAndBlankPreviousSerials(string? previousSerial)
    {
        var devices = new[]
        {
            Device("device-a"),
            Device("device-b"),
        };

        var result = DeviceTargetSelection.Resolve(devices, previousSerial, "device-b");

        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_IgnoresNullAndBlankWorkspaceSerials(string? workspaceSerial)
    {
        var devices = new[]
        {
            Device("device-a"),
            Device("device-b"),
        };

        var result = DeviceTargetSelection.Resolve(devices, "missing", workspaceSerial);

        Assert.Equal("device-a", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_ComparesPreviousSerialsWithOrdinalComparison()
    {
        var devices = new[]
        {
            Device("device-b"),
            Device("device-a"),
        };

        var result = DeviceTargetSelection.Resolve(devices, "DEVICE-A", null);

        Assert.Equal(ReverseSerials, result.Usable.Select(device => device.Serial));
        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_ComparesWorkspaceSerialsWithOrdinalComparison()
    {
        var devices = new[]
        {
            Device("device-b"),
            Device("device-a"),
        };

        var result = DeviceTargetSelection.Resolve(devices, null, "DEVICE-A");

        Assert.Equal("device-b", result.Selected?.Serial);
    }

    [Fact]
    public void Resolve_KeepsDuplicateSerialsInInputOrderAndSelectsTheFirstMatch()
    {
        var first = Device("same");
        var second = Device("same");
        var third = Device("other");

        var result = DeviceTargetSelection.Resolve([first, second, third], "same", null);

        Assert.Equal(new[] { first, second, third }, result.Usable);
        Assert.Same(first, result.Selected);
    }

    [Fact]
    public void Resolve_DoesNotMutateTheInput()
    {
        var devices = new List<DeviceInfo>
        {
            Device("device-a", DeviceState.Offline),
            Device("device-b"),
        };
        var snapshot = devices.ToArray();

        _ = DeviceTargetSelection.Resolve(devices, null, null);

        Assert.Equal(snapshot, devices);
    }

    private static DeviceInfo Device(string serial, DeviceState state = DeviceState.Device) =>
        new() { Serial = serial, State = state };
}
