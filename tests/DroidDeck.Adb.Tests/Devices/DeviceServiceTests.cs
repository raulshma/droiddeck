using DroidDeck.Adb.Devices;
using DroidDeck.Adb.Tracking;
using DroidDeck.Core;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.TestSupport;

namespace DroidDeck.Adb.Tests.Devices;

public sealed class DeviceServiceTests
{
    private const string PixelLine =
        "S1 device product:panther model:Pixel_7 device:panther transport_id:1";

    [Fact]
    public async Task StartAsync_MergesTransportAndMetadata()
    {
        await using var fixture = new Fixture();
        fixture.Metadata.Response = _ => CreateMetadata(batteryLevel: 73);
        var service = await fixture.StartAsync(PixelLine);

        await Wait.UntilAsync(() => service.Devices.Count == 1 && service.Devices[0].BatteryLevel == 73);

        var device = service.Devices[0];
        Assert.Equal("S1", device.Serial);
        Assert.Equal(DeviceState.Device, device.State);
        Assert.Equal("Pixel 7", device.Model);
        Assert.Equal("Google", device.Manufacturer);
        Assert.Equal("14", device.AndroidVersion);
        Assert.Equal(34, device.ApiLevel);
        Assert.Equal("arm64-v8a", device.Abi);
        Assert.Equal(73, device.BatteryLevel);
        Assert.Equal("1080x2400", device.ScreenSize);
        Assert.Equal(420, device.Density);
        Assert.Equal("192.168.1.5", device.IpAddress);
        Assert.Equal("1", device.TransportId);
        Assert.Equal(DeviceConnectionKind.Usb, device.ConnectionKind);
        Assert.Equal(128L * 1024 * 1024 * 1024, device.StorageTotalBytes);
        Assert.Equal(64L * 1024 * 1024 * 1024, device.StorageFreeBytes);
        Assert.NotNull(device.MetadataUpdatedAt);
        Assert.True(device.CanRunActions);
        Assert.True(service.IsAdbConnected);
        Assert.False(service.IsUsingCliFallback);
        Assert.Contains("S1", fixture.Metadata.Requests);
    }

    [Fact]
    public async Task StartAsync_ExposesUnauthorizedGuidance()
    {
        await using var fixture = new Fixture();
        var service = await fixture.StartAsync("S9 unauthorized model:Tablet");

        await Wait.UntilAsync(() => service.Devices.Count == 1);
        var device = service.Devices[0];

        Assert.Equal(DeviceState.Unauthorized, device.State);
        Assert.False(device.CanRunActions);
        Assert.Contains(
            "Allow USB debugging",
            DeviceStateGuidance.For(device.State),
            StringComparison.OrdinalIgnoreCase);
        await Task.Delay(100);
        Assert.Empty(fixture.Metadata.Requests);
    }

    [Fact]
    public async Task NotesAndTags_PersistAndAreAppliedToDevices()
    {
        await using var fixture = new Fixture();
        var service = await fixture.StartAsync(PixelLine);
        await Wait.UntilAsync(() => service.Devices.Count == 1);

        await service.SetNotesAsync("S1", "Lab phone");
        await service.SetTagsAsync("S1", ["android", " Android ", "pixel", "  "]);

        var device = await service.GetDeviceAsync("S1");
        Assert.NotNull(device);
        Assert.Equal("Lab phone", device.Notes);
        Assert.Equal(["android", "pixel"], device.Tags);
        Assert.Equal("Lab phone", service.Devices[0].Notes);
        Assert.True(File.Exists(Path.Combine(fixture.Temp.Path, "devices", "S1.json")));
    }

    [Fact]
    public async Task SetNotes_WithNull_ClearsNotes()
    {
        await using var fixture = new Fixture();
        var service = await fixture.StartAsync(PixelLine);
        await Wait.UntilAsync(() => service.Devices.Count == 1);
        await service.SetNotesAsync("S1", "Temporary");

        await service.SetNotesAsync("S1", null);

        var device = await service.GetDeviceAsync("S1");
        Assert.Null(device!.Notes);
    }

    [Fact]
    public async Task DeviceUpdated_IsRaisedForRecordChanges()
    {
        await using var fixture = new Fixture();
        var service = await fixture.StartAsync(PixelLine);
        await Wait.UntilAsync(() => service.Devices.Count == 1);
        var updates = new List<DeviceInfo>();
        service.DeviceUpdated += (_, device) =>
        {
            lock (updates)
            {
                updates.Add(device);
            }
        };

        await service.SetTagsAsync("S1", ["bench"]);

        lock (updates)
        {
            Assert.Contains(updates, static device => device.Tags.Contains("bench"));
        }
    }

    [Fact]
    public async Task Records_AreReappliedAfterRestart()
    {
        using var temp = new TempDirectory();

        await using (var first = new Fixture(temp))
        {
            var service = await first.StartAsync(PixelLine);
            await Wait.UntilAsync(() => service.Devices.Count == 1);
            await service.SetNotesAsync("S1", "Persisted");
            await service.SetTagsAsync("S1", ["lab"]);
        }

        await using (var second = new Fixture(temp))
        {
            var service = await second.StartAsync(PixelLine);
            await Wait.UntilAsync(() => service.Devices.Count == 1);
            var device = service.Devices[0];
            Assert.Equal("Persisted", device.Notes);
            Assert.Equal(["lab"], device.Tags);
        }
    }

    [Fact]
    public async Task StartAsync_AppliesStoredRecordsForKnownSerials()
    {
        await using var fixture = new Fixture();
        await fixture.Store.SaveAsync(new DeviceRecord
        {
            Serial = "S1",
            Notes = "Bench",
            Tags = ["lab"],
        });

        var service = await fixture.StartAsync(PixelLine);

        await Wait.UntilAsync(() => service.Devices.Count == 1);
        Assert.Equal("Bench", service.Devices[0].Notes);
        Assert.Equal(["lab"], service.Devices[0].Tags);
    }

    [Fact]
    public async Task DisconnectedDevices_AreRemovedFromTheSnapshot()
    {
        await using var fixture = new Fixture();
        var service = await fixture.StartAsync(PixelLine);
        await Wait.UntilAsync(() => service.Devices.Count == 1);

        await fixture.Server.SetDevicesAsync();

        await Wait.UntilAsync(() => service.Devices.Count == 0);
    }

    [Fact]
    public async Task RefreshMetadataAsync_UpdatesTheDevice()
    {
        await using var fixture = new Fixture();
        fixture.Metadata.Response = _ => CreateMetadata(batteryLevel: 73);
        var service = await fixture.StartAsync(PixelLine);
        await Wait.UntilAsync(() => service.Devices.Count == 1 && service.Devices[0].BatteryLevel == 73);

        fixture.Metadata.Response = _ => CreateMetadata(batteryLevel: 55);
        var metadata = await service.RefreshMetadataAsync("S1");

        Assert.Equal(55, metadata.BatteryLevel);
        Assert.Equal(55, service.Devices[0].BatteryLevel);
        Assert.Equal("14", service.Devices[0].AndroidVersion);
    }

    [Fact]
    public async Task RestartAdbAsync_RestartsServerAndReconnects()
    {
        await using var fixture = new Fixture();
        fixture.Runner.Handler = request =>
        {
            if (request.Arguments.Contains("kill-server"))
            {
                fixture.Server.DropConnections();
            }

            return new ProcessResult { CommandLine = request.ToCommandLine(), ExitCode = 0 };
        };
        var service = await fixture.StartAsync(PixelLine);
        await Wait.UntilAsync(() => service.Devices.Count == 1 && service.IsAdbConnected);

        var restarted = await service.RestartAdbAsync();

        Assert.True(restarted);
        Assert.Contains(fixture.Runner.Requests, static request => request.Arguments.Contains("kill-server"));
        Assert.Contains(fixture.Runner.Requests, static request => request.Arguments.Contains("start-server"));
        await Wait.UntilAsync(() => service.IsAdbConnected && service.Devices.Count == 1);
    }

    [Fact]
    public async Task StartAsync_UsesCliFallbackWhenSocketIsUnavailable()
    {
        await using var fixture = new Fixture();
        fixture.Server.Pause();
        fixture.Runner.Handler = request => request.Arguments.Contains("devices")
            ? new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                ExitCode = 0,
                StandardOutput = "List of devices attached\n" + PixelLine + "\n",
            }
            : new ProcessResult { CommandLine = request.ToCommandLine(), ExitCode = 0 };

        var service = await fixture.StartAsync();
        await Wait.UntilAsync(() => service.Devices.Count == 1 && service.IsUsingCliFallback);

        Assert.True(service.IsAdbConnected);
        Assert.Equal("Pixel 7", service.Devices[0].Model);
        Assert.Contains(fixture.Metadata.Requests, static serial => serial == "S1");
    }

    [Fact]
    public async Task GetDeviceAsync_ReturnsNullForUnknownSerial()
    {
        await using var fixture = new Fixture();
        var service = await fixture.StartAsync();
        Assert.Null(await service.GetDeviceAsync("missing"));
    }

    private static DeviceMetadata CreateMetadata(int batteryLevel) => new()
    {
        Manufacturer = "Google",
        AndroidVersion = "14",
        ApiLevel = 34,
        Abi = "arm64-v8a",
        AbiList = ["arm64-v8a", "armeabi-v7a"],
        BatteryLevel = batteryLevel,
        BatteryStatus = "Charging",
        ScreenSize = "1080x2400",
        Density = 420,
        IpAddress = "192.168.1.5",
        BuildType = "userdebug",
        IsDebuggable = true,
        Characteristics = "default",
        StorageTotalBytes = 128L * 1024 * 1024 * 1024,
        StorageFreeBytes = 64L * 1024 * 1024 * 1024,
        FetchedAt = DateTimeOffset.UtcNow,
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly bool _ownsTemp;
        private JsonDeviceRecordStore? _store;
        private DeviceService? _service;

        public Fixture(TempDirectory? temp = null)
        {
            _ownsTemp = temp is null;
            Temp = temp ?? new TempDirectory();
        }

        public FakeAdbServer Server { get; } = new();

        public StubProcessRunner Runner { get; } = new();

        public StubMetadataSource Metadata { get; } = new();

        public TempDirectory Temp { get; }

        public JsonDeviceRecordStore Store =>
            _store ??= new JsonDeviceRecordStore(new AppPaths(Temp.Path));

        public async Task<DeviceService> StartAsync(params string[] deviceLines)
        {
            await Server.SetDevicesAsync(deviceLines);
            var tracker = new AdbDeviceTracker(
                Runner,
                new AdbTrackerOptions
                {
                    Endpoint = Server.Endpoint,
                    Debounce = TimeSpan.FromMilliseconds(10),
                    ConnectTimeout = TimeSpan.FromMilliseconds(500),
                    ReconnectDelays = [TimeSpan.FromMilliseconds(20)],
                    ProcessPollInterval = TimeSpan.FromMilliseconds(50),
                    ProcessTimeout = TimeSpan.FromSeconds(2),
                });
            _service = new DeviceService(tracker, Store, Metadata);
            await _service.StartAsync();
            return _service;
        }

        public async ValueTask DisposeAsync()
        {
            if (_service is not null)
            {
                await _service.DisposeAsync();
            }

            await Server.DisposeAsync();
            if (_ownsTemp)
            {
                Temp.Dispose();
            }
        }
    }

    private sealed class StubMetadataSource : IDeviceMetadataSource
    {
        private readonly object _gate = new();
        private readonly List<string> _requests = [];

        public Func<string, DeviceMetadata> Response { get; set; } =
            _ => new DeviceMetadata { FetchedAt = DateTimeOffset.UtcNow };

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task<DeviceMetadata> FetchAsync(string serial, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _requests.Add(serial);
            }

            return Task.FromResult(Response(serial));
        }
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly object _gate = new();
        private readonly List<ProcessRequest> _requests = [];

        public Func<ProcessRequest, ProcessResult>? Handler { get; set; }

        public IReadOnlyList<ProcessRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _requests.Add(request);
            }

            return Task.FromResult(
                Handler?.Invoke(request) ?? new ProcessResult
                {
                    CommandLine = request.ToCommandLine(),
                    ExitCode = 0,
                });
        }
    }
}
