using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Resources;
using DroidDeck.Core.Screen;

namespace DroidDeck.Screen.Tests;

public sealed class ScrcpyScreenServiceTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeScreenWindowController _windows = new();
    private readonly ResourceGuard _guard = new();

    public ScrcpyScreenServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "droiddeck-screen-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        _guard.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task StartAsync_LaunchesDockedMirrorAndTracksSession()
    {
        await using var service = CreateService();
        var options = new ScreenStartOptions
        {
            HostingMode = ScreenHostingMode.DockedCompanion,
            Bounds = new ScreenBounds(15, 25, 900, 700),
        };

        var result = await service.StartAsync("17bd753c", options);

        Assert.True(result.Succeeded);
        Assert.Null(result.Notice);
        Assert.Equal("scrcpy", _launcher.Requests[0].FileName);
        Assert.Contains("--window-borderless", _launcher.Requests[0].Arguments);
        Assert.Contains("--window-x=15", _launcher.Requests[0].Arguments);
        var session = Assert.Single(service.ActiveSessions);
        Assert.Equal("17bd753c", session.Serial);
        Assert.Equal(ScreenHostingMode.DockedCompanion, session.HostingMode);
        Assert.Equal(ScreenSessionState.Running, session.State);
        Assert.Equal(1, _guard.Snapshot.Counts[ResourceKind.Mirror]);
    }

    [Fact]
    public async Task StartAsync_StartFailureFallsBackToExternalWindow()
    {
        _launcher.StartFailuresRemaining = 1;
        await using var service = CreateService();
        var options = new ScreenStartOptions
        {
            HostingMode = ScreenHostingMode.DockedCompanion,
            Bounds = new ScreenBounds(15, 25, 900, 700),
        };

        var result = await service.StartAsync("serial", options);

        Assert.True(result.Succeeded);
        Assert.Contains("external window", result.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--window-borderless", Assert.Single(_launcher.Requests).Arguments);
        Assert.Equal(ScreenHostingMode.ExternalWindow, Assert.Single(service.ActiveSessions).HostingMode);
    }

    [Fact]
    public async Task StartAsync_ImmediateExitReportsFailureAndReleasesLease()
    {
        _launcher.ExitImmediately = true;
        await using var service = CreateService();
        var options = new ScreenStartOptions
        {
            HostingMode = ScreenHostingMode.DockedCompanion,
            Bounds = new ScreenBounds(15, 25, 900, 700),
        };

        var result = await service.StartAsync("serial", options);

        Assert.False(result.Succeeded);
        Assert.Contains("exited immediately", result.Message, StringComparison.Ordinal);
        Assert.Contains("Install scrcpy", result.Guidance, StringComparison.Ordinal);
        Assert.Empty(service.ActiveSessions);
        Assert.Empty(_guard.Snapshot.Active);
    }

    [Fact]
    public async Task StartAsync_WhenMirrorLimitReachedExplainsConsumers()
    {
        _guard.UpdateLimits(new ResourceLimits { MaxMirrors = 1 });
        await using var service = CreateService();
        await service.StartAsync("first");

        var result = await service.StartAsync("second");

        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first", result.Guidance, StringComparison.Ordinal);
        Assert.Single(service.ActiveSessions);
    }

    [Fact]
    public async Task StopAsync_ClosesWindowGracefullyAndReleasesResources()
    {
        _windows.CloseResult = true;
        _windows.OnClose = _launcher.Exit;
        await using var service = CreateService();
        await service.StartAsync("serial");

        var result = await service.StopAsync("serial");

        Assert.True(result.Succeeded);
        Assert.Empty(_launcher.Terminated);
        Assert.Empty(service.ActiveSessions);
        Assert.Empty(_guard.Snapshot.Active);
    }

    [Fact]
    public async Task StopAsync_TerminatesWhenWindowCannotBeClosed()
    {
        await using var service = CreateService();
        await service.StartAsync("serial");

        var result = await service.StopAsync("serial");

        Assert.True(result.Succeeded);
        Assert.Single(_launcher.Terminated);
        Assert.Empty(service.ActiveSessions);
    }

    [Fact]
    public async Task UpdateBoundsAndFocus_DelegateToWindowController()
    {
        await using var service = CreateService();
        await service.StartAsync("serial");

        var move = await service.UpdateBoundsAsync("serial", new ScreenBounds(1, 2, 300, 400));
        var focus = await service.FocusAsync("serial");

        Assert.True(move.Succeeded);
        Assert.True(focus.Succeeded);
        Assert.Equal(new ScreenBounds(1, 2, 300, 400), Assert.Single(_windows.Moves).Bounds);
        Assert.Single(_windows.Focused);
    }

    [Fact]
    public async Task FocusAsync_KeepsTryingWhileScrcpyCreatesItsWindow()
    {
        await using var service = CreateService();
        await service.StartAsync("serial");
        _windows.FocusFailuresRemaining = 3;

        var result = await service.FocusAsync("serial");

        Assert.True(result.Succeeded);
        Assert.Equal(4, _windows.Focused.Count);
    }

    [Fact]
    public async Task FocusAsync_GivesUpWhenTheWindowNeverAppears()
    {
        _windows.FocusResult = false;
        await using var service = CreateService(windowWait: TimeSpan.Zero);
        await service.StartAsync("serial");

        var result = await service.FocusAsync("serial");

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.Message, StringComparison.Ordinal);
        Assert.Single(_windows.Focused);
    }

    [Fact]
    public async Task UpdateBoundsAsync_KeepsTryingWhileScrcpyCreatesItsWindow()
    {
        await using var service = CreateService();
        await service.StartAsync("serial");
        _windows.MoveFailuresRemaining = 2;

        var result = await service.UpdateBoundsAsync("serial", new ScreenBounds(1, 2, 300, 400));

        Assert.True(result.Succeeded);
        Assert.Equal(3, _windows.Moves.Count);
        Assert.Null(Assert.Single(service.ActiveSessions).Notice);
    }

    [Fact]
    public async Task PressKeyAsync_MapsKeyAndShowsCommand()
    {
        await using var service = CreateService();

        var result = await service.PressKeyAsync("serial", DeviceControlKey.Home);

        Assert.True(result.Succeeded);
        var request = Assert.Single(_runner.Requests);
        Assert.Equal(["shell", "input", "keyevent", "KEYCODE_HOME"], request.Arguments.Skip(2));
        Assert.Contains("KEYCODE_HOME", result.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenshotAsync_CapturesPullsAndReturnsLocalPath()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        _runner.Handler = request =>
        {
            if (request.Arguments.Contains("pull"))
            {
                File.WriteAllBytes(request.Arguments[^1], png);
            }

            return FakeProcessRunner.Success(request);
        };
        await using var service = CreateService();

        var result = await service.ScreenshotAsync("serial");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Path);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(png, await File.ReadAllBytesAsync(result.Path));
        Assert.Contains(_runner.Requests, request => request.Arguments.Contains("screencap"));
    }

    [Fact]
    public async Task StartAndStopRecording_UsesHeadlessMkvAndReleasesLease()
    {
        await using var service = CreateService();
        await service.StartAsync("serial");
        var recordings = Path.Combine(_root, "recordings");

        var started = await service.StartRecordingAsync("serial", recordings);

        Assert.True(started.Succeeded);
        Assert.NotNull(started.Path);
        Assert.EndsWith(".mkv", started.Path, StringComparison.Ordinal);
        var recorderRequest = _launcher.Requests[^1];
        Assert.Contains("--no-playback", recorderRequest.Arguments);
        Assert.Contains(recorderRequest.Arguments, argument => argument.StartsWith("--record=", StringComparison.Ordinal));
        Assert.True(Assert.Single(service.ActiveSessions).IsRecording);
        Assert.Single(_windows.HiddenProcesses);
        Assert.Equal(1, _guard.Snapshot.Counts[ResourceKind.Recording]);
        _windows.CloseResult = true;
        _windows.OnClose = _launcher.Exit;

        var stopped = await service.StopRecordingAsync("serial");

        Assert.True(stopped.Succeeded);
        Assert.Equal(started.Path, stopped.Path);
        Assert.Empty(_launcher.Terminated);
        Assert.False(Assert.Single(service.ActiveSessions).IsRecording);
        Assert.False(_guard.Snapshot.Counts.ContainsKey(ResourceKind.Recording));
    }

    [Fact]
    public async Task UnexpectedMirrorExitFinalizesTheRecordingAndReleasesLeases()
    {
        await using var service = CreateService();
        await service.StartAsync("serial");
        var started = await service.StartRecordingAsync("serial", Path.Combine(_root, "recordings"));
        Assert.NotNull(started.Path);
        _windows.CloseResult = true;
        _windows.OnClose = _launcher.Exit;
        _launcher.Exit(Assert.Single(service.ActiveSessions).ProcessId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while ((service.ActiveSessions.Count > 0 ||
                _guard.Snapshot.Counts.ContainsKey(ResourceKind.Mirror) ||
                _guard.Snapshot.Counts.ContainsKey(ResourceKind.Recording)) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Empty(service.ActiveSessions);
        Assert.False(_guard.Snapshot.Counts.ContainsKey(ResourceKind.Mirror));
        Assert.False(_guard.Snapshot.Counts.ContainsKey(ResourceKind.Recording));
    }

    [Fact]
    public async Task RecordingRequiresAnActiveMirror()
    {
        await using var service = CreateService();

        var result = await service.StartRecordingAsync("serial");

        Assert.False(result.Succeeded);
        Assert.Contains("not mirroring", result.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Requests);
    }

    [Fact]
    public async Task DisposeAsync_StopsEverySessionAndRecording()
    {
        await using (var service = CreateService())
        {
            await service.StartAsync("first");
            await service.StartAsync("second");
            await service.StartRecordingAsync("first", Path.Combine(_root, "recordings"));

            await service.DisposeAsync();

            Assert.Empty(service.ActiveSessions);
            Assert.Empty(_guard.Snapshot.Active);
        }
    }

    private ScrcpyScreenService CreateService(TimeSpan? windowWait = null)
    {
        _windows.OnClose ??= _launcher.Exit;
        return new ScrcpyScreenService(
            _runner,
            _launcher,
            _windows,
            _guard,
            _paths,
            () => "scrcpy",
            () => "adb",
            () => new ScreenSettings(),
            pollInterval: TimeSpan.FromMilliseconds(10),
            windowWait: windowWait);
    }
}
