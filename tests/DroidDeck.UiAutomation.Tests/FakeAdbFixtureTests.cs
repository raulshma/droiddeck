using DroidDeck.Adb;
using DroidDeck.Adb.Devices;
using DroidDeck.Adb.Protocol;
using DroidDeck.TestSupport;

namespace DroidDeck.UiAutomation.Tests;

public sealed class FakeAdbFixtureTests
{
    [Theory]
    [InlineData("ready", "device")]
    [InlineData("offline", "offline")]
    [InlineData("unauthorized", "unauthorized")]
    public async Task DevicesCommandReportsScenarioState(string scenario, string state)
    {
        using var temp = new TempDirectory("fakeadb-cli");
        var result = await FakeAdbCli.RunAsync(
            "--state-dir", temp.Path,
            "--scenario", scenario,
            "--serial", "FAKE-UI-01",
            "devices", "-l");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("List of devices attached", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"FAKE-UI-01 {state}", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataBatchParsesWithTheProductionParser()
    {
        using var temp = new TempDirectory("fakeadb-metadata");
        var result = await FakeAdbCli.RunAsync(
            "--state-dir", temp.Path,
            "--serial", "FAKE-UI-01",
            "-s", "FAKE-UI-01",
            "shell", AdbDeviceMetadataSource.BatchScript);

        Assert.Equal(0, result.ExitCode);
        var metadata = AdbDeviceMetadataSource.ParseBatch(result.StandardOutput, DateTimeOffset.UnixEpoch);
        Assert.Equal("Google", metadata.Manufacturer);
        Assert.Equal("Pixel 8", metadata.Model);
        Assert.Equal("15", metadata.AndroidVersion);
        Assert.Equal(35, metadata.ApiLevel);
        Assert.Equal("1080x2400", metadata.ScreenSize);
        Assert.Equal(420, metadata.Density);
        Assert.Equal(87, metadata.BatteryLevel);
        Assert.Equal("Charging", metadata.BatteryStatus);
        Assert.Equal("192.168.1.42", metadata.IpAddress);
        Assert.NotNull(metadata.StorageTotalBytes);
        Assert.NotNull(metadata.StorageFreeBytes);
        Assert.Empty(metadata.Warnings);
    }

    [Fact]
    public async Task MetadataFailureScenarioReturnsAnError()
    {
        using var temp = new TempDirectory("fakeadb-metadata-failure");
        var result = await FakeAdbCli.RunAsync(
            "--state-dir", temp.Path,
            "--scenario", "metadata-failure",
            "-s", "FAKE-UI-01",
            "shell", AdbDeviceMetadataSource.BatchScript);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("device offline", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAndLogcatCommandsAreServed()
    {
        using var temp = new TempDirectory("fakeadb-actions");
        var install = await FakeAdbCli.RunAsync(
            "--state-dir", temp.Path,
            "-s", "FAKE-UI-01",
            "install", "app.apk");
        var logcat = await FakeAdbCli.RunAsync(
            "--state-dir", temp.Path,
            "-s", "FAKE-UI-01",
            "logcat", "-d");

        Assert.Equal(0, install.ExitCode);
        Assert.Contains("Success", install.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(0, logcat.ExitCode);
        Assert.Contains("beginning of main", logcat.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerStreamsHotpluggedDevices()
    {
        using var temp = new TempDirectory("fakeadb-server");
        await using var server = await FakeAdbServerProcess.StartAsync(temp.Path, "hotplug", "FAKE-HOT-01");
        var endpoint = new AdbEndpoint("127.0.0.1", server.Port);
        var client = new AdbSocketClient(TimeSpan.FromSeconds(5));

        await using var session = await client.TrackDevicesAsync(endpoint);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Equal(string.Empty, await session.ReadFrameAsync(timeout.Token));

        WriteDevices(temp.Path, "FAKE-HOT-01 device product:shiba model:Pixel_8 device:shiba transport_id:1");

        string? observed = null;
        while (!timeout.IsCancellationRequested)
        {
            var frame = await session.ReadFrameAsync(timeout.Token);
            if (frame is null)
            {
                break;
            }

            if (frame.Contains("FAKE-HOT-01", StringComparison.Ordinal))
            {
                observed = frame;
                break;
            }
        }

        Assert.True(
            observed?.Contains("FAKE-HOT-01 device", StringComparison.Ordinal) == true,
            $"The hotplugged device was not broadcast. Last frame: '{observed}'.");

        var listing = await client.SendAsync(endpoint, "host:devices-l");
        Assert.Contains("FAKE-HOT-01 device", listing, StringComparison.Ordinal);
    }

    private static void WriteDevices(string directory, params string[] lines)
    {
        var path = Path.Combine(directory, "devices.txt");
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, overwrite: true);
    }
}
