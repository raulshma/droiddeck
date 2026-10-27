using System.Collections.Concurrent;
using System.Diagnostics;
using DroidDeck.Adb.Tracking;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.TestSupport;

namespace DroidDeck.Adb.Tests.Tracking;

public sealed class AdbDeviceTrackerTests
{
    private const string PixelLine =
        "S1 device product:panther model:Pixel_7 device:panther transport_id:1";

    [Fact]
    public async Task StartAsync_TracksInitialDevicesAndUpdates()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(PixelLine);
        await using var tracker = CreateTracker(server, out _);

        await tracker.StartAsync();

        await Wait.UntilAsync(() => tracker.Devices.Count == 1);
        Assert.True(tracker.IsConnected);
        Assert.False(tracker.IsUsingProcessFallback);
        var initial = tracker.Devices[0];
        Assert.Equal("S1", initial.Serial);
        Assert.Equal("Pixel 7", initial.Model);
        Assert.Equal(DeviceState.Device, initial.State);

        var stopwatch = Stopwatch.StartNew();
        await server.SetDevicesAsync(PixelLine, "S2 unauthorized model:Tablet");
        await Wait.UntilAsync(() => tracker.Devices.Count == 2);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(300),
            $"The hotplug update took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
        Assert.Equal(DeviceState.Unauthorized, tracker.Devices.Single(static device => device.Serial == "S2").State);
    }

    [Fact]
    public async Task StartAsync_BootstrapsServerOnSocketFailure()
    {
        await using var server = new FakeAdbServer();
        server.Pause();
        var runner = new StubProcessRunner();
        var options = CreateOptions(server.Endpoint) with { EnableProcessFallback = false };
        await using var tracker = new AdbDeviceTracker(runner, options);

        await tracker.StartAsync();

        await Wait.UntilAsync(() => runner.Requests.Any(
            static request => request.Arguments.Contains("start-server")));
        Assert.False(tracker.IsConnected);
        Assert.NotNull(tracker.LastError);
    }

    [Fact]
    public async Task StartAsync_FallsBackToProcessWhenSocketUnavailable()
    {
        await using var server = new FakeAdbServer();
        server.Pause();
        var runner = new StubProcessRunner
        {
            Handler = request => request.Arguments.Contains("devices")
                ? StubProcessRunner.Success(request, "List of devices attached\n" + PixelLine + "\n")
                : StubProcessRunner.Success(request),
        };
        await using var tracker = new AdbDeviceTracker(runner, CreateOptions(server.Endpoint));

        await tracker.StartAsync();

        await Wait.UntilAsync(() => tracker.IsUsingProcessFallback && tracker.Devices.Count == 1);
        Assert.True(tracker.IsConnected);
        Assert.Null(tracker.LastError);
        Assert.Contains(runner.Requests, static request => request.Arguments.Contains("start-server"));
    }

    [Fact]
    public async Task Fallback_RecoversToSocketWhenServerReturns()
    {
        await using var server = new FakeAdbServer();
        server.Pause();
        var runner = new StubProcessRunner
        {
            Handler = request => request.Arguments.Contains("devices")
                ? StubProcessRunner.Success(request, "List of devices attached\n" + PixelLine + "\n")
                : StubProcessRunner.Success(request),
        };
        await using var tracker = new AdbDeviceTracker(runner, CreateOptions(server.Endpoint));

        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.IsUsingProcessFallback && tracker.Devices.Count == 1);

        server.Resume();

        await Wait.UntilAsync(() => !tracker.IsUsingProcessFallback && tracker.IsConnected);
        await server.SetDevicesAsync("S2 device product:panther model:Pixel_8 device:panther transport_id:2");
        await Wait.UntilAsync(() => tracker.Devices.Any(static device => device.Serial == "S2"));
    }

    [Fact]
    public async Task RestartServerAsync_RunsKillAndStartAndReconnects()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(PixelLine);
        var runner = new StubProcessRunner();
        runner.Handler = request =>
        {
            if (request.Arguments.Contains("kill-server"))
            {
                server.DropConnections();
            }

            return StubProcessRunner.Success(request);
        };
        await using var tracker = new AdbDeviceTracker(runner, CreateOptions(server.Endpoint));
        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.Devices.Count == 1 && tracker.IsConnected);

        var restarted = await tracker.RestartServerAsync();

        Assert.True(restarted);
        Assert.Contains(runner.Requests, static request => request.Arguments.Contains("kill-server"));
        Assert.Contains(runner.Requests, static request => request.Arguments.Contains("start-server"));
        await Wait.UntilAsync(() => tracker.IsConnected && tracker.Devices.Count == 1);
    }

    [Fact]
    public async Task RestartServerAsync_ReportsStartFailure()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(PixelLine);
        var runner = new StubProcessRunner
        {
            Handler = request => request.Arguments.Contains("start-server")
                ? new ProcessResult { CommandLine = request.ToCommandLine(), ExitCode = 1, StandardError = "adb: failed to start" }
                : StubProcessRunner.Success(request),
        };
        await using var tracker = new AdbDeviceTracker(runner, CreateOptions(server.Endpoint));
        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.IsConnected);

        var restarted = await tracker.RestartServerAsync();

        Assert.False(restarted);
        Assert.Contains("adb: failed to start", tracker.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_StopsPublishingAndDisconnects()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(PixelLine);
        await using var tracker = CreateTracker(server, out _);
        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.Devices.Count == 1);

        await tracker.StopAsync();
        await server.SetDevicesAsync("S2 device model:Late");
        await Task.Delay(150);

        Assert.False(tracker.IsConnected);
        Assert.Single(tracker.Devices);
        Assert.Equal("S1", tracker.Devices[0].Serial);
    }

    [Fact]
    public async Task RefreshAsync_QueriesTheServerImmediately()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(PixelLine);
        await using var tracker = CreateTracker(server, out _);
        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.Devices.Count == 1);

        await tracker.RefreshAsync();

        await Wait.UntilAsync(() => tracker.Devices.Count == 1);
        Assert.True(tracker.IsConnected);
    }

    [Fact]
    public async Task ConnectionChanged_ReportsFallbackAndRecovery()
    {
        await using var server = new FakeAdbServer();
        server.Pause();
        var runner = new StubProcessRunner
        {
            Handler = request => request.Arguments.Contains("devices")
                ? StubProcessRunner.Success(request, "List of devices attached\n" + PixelLine + "\n")
                : StubProcessRunner.Success(request),
        };
        await using var tracker = new AdbDeviceTracker(runner, CreateOptions(server.Endpoint));
        var events = new ConcurrentQueue<AdbConnectionChangedEventArgs>();
        tracker.ConnectionChanged += (_, args) => events.Enqueue(args);

        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.IsUsingProcessFallback);

        server.Resume();
        await Wait.UntilAsync(() => tracker.IsConnected && !tracker.IsUsingProcessFallback);

        Assert.Contains(events, static args => args.IsUsingProcessFallback);
        Assert.Contains(events, static args => args.IsConnected && !args.IsUsingProcessFallback);
    }

    [Fact]
    public async Task UnauthorizedDevice_ReportsGuidance()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync("S9 unauthorized model:Pixel");
        await using var tracker = CreateTracker(server, out _);
        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.Devices.Count == 1);

        var device = Assert.Single(tracker.Devices);
        Assert.Equal(DeviceState.Unauthorized, device.State);
        Assert.Contains(
            "Allow USB debugging",
            DeviceStateGuidance.For(device.State),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_IsIdempotent()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(PixelLine);
        await using var tracker = CreateTracker(server, out _);

        await tracker.StartAsync();
        await tracker.StartAsync();
        await Wait.UntilAsync(() => tracker.Devices.Count == 1);
    }

    [Fact]
    public async Task DisposeAsync_IsSafeWithoutStart()
    {
        await using var server = new FakeAdbServer();
        var tracker = new AdbDeviceTracker(
            new StubProcessRunner(),
            CreateOptions(server.Endpoint));

        await tracker.DisposeAsync();
        await tracker.DisposeAsync();
    }

    private static AdbDeviceTracker CreateTracker(FakeAdbServer server, out StubProcessRunner runner)
    {
        runner = new StubProcessRunner();
        return new AdbDeviceTracker(runner, CreateOptions(server.Endpoint));
    }

    private static AdbTrackerOptions CreateOptions(AdbEndpoint endpoint) => new()
    {
        Endpoint = endpoint,
        Debounce = TimeSpan.FromMilliseconds(10),
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        ReconnectDelays = [TimeSpan.FromMilliseconds(20)],
        ProcessPollInterval = TimeSpan.FromMilliseconds(50),
        ProcessTimeout = TimeSpan.FromSeconds(2),
    };

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

            return Task.FromResult(Handler?.Invoke(request) ?? Success(request));
        }

        public static ProcessResult Success(ProcessRequest request, string standardOutput = "") => new()
        {
            CommandLine = request.ToCommandLine(),
            ExitCode = 0,
            StandardOutput = standardOutput,
        };
    }
}
