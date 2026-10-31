using DroidDeck.Core.Screen;

namespace DroidDeck.Screen.Tests;

public sealed class AdbThumbnailServiceTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 9, 8, 7];

    private readonly string _root;
    private readonly FakeProcessRunner _runner = new();
    private readonly FakeScreenService _screens = new();

    public AdbThumbnailServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "droiddeck-thumb-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task CaptureAsync_PullsPngAndCleansStagingFile()
    {
        _runner.Handler = WritePulledFile;
        using var service = new AdbThumbnailService(
            _runner,
            _screens,
            () => "adb",
            Path.Combine(_root, "staging"));

        var thumbnail = await service.CaptureAsync("17bd753c");

        Assert.Equal("17bd753c", thumbnail.Serial);
        Assert.Equal(Png, thumbnail.Png);
        Assert.Contains("17bd753c", thumbnail.CommandLine, StringComparison.Ordinal);
        Assert.Contains(_runner.Requests, request => request.Arguments.Contains("screencap"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task CaptureAsync_HoldsAThumbnailLeaseForTheResourceGuard()
    {
        using var guard = new DroidDeck.Core.Resources.ResourceGuard(
            new DroidDeck.Core.Resources.ResourceLimits { MaxMirrors = 2 });
        var activeDuringCapture = 0;
        _runner.Handler = request =>
        {
            if (request.Arguments.Contains("screencap"))
            {
                activeDuringCapture = guard.Snapshot.Counts.GetValueOrDefault(
                    DroidDeck.Core.Resources.ResourceKind.Thumbnail);
            }

            return WritePulledFile(request);
        };
        using var service = new AdbThumbnailService(
            _runner,
            _screens,
            () => "adb",
            Path.Combine(_root, "staging"),
            resourceGuard: guard);

        await service.CaptureAsync("serial");

        Assert.Equal(1, activeDuringCapture);
        Assert.DoesNotContain(
            guard.Snapshot.Active,
            lease => lease.Kind == DroidDeck.Core.Resources.ResourceKind.Thumbnail);
    }

    [Fact]
    public async Task CaptureAsync_ThrowsActionableMessageWhenScreencapFails()
    {
        _runner.Handler = request => request.Arguments.Contains("screencap")
            ? FakeProcessRunner.Failure(request, "error: device unauthorized")
            : FakeProcessRunner.Success(request);
        using var service = new AdbThumbnailService(
            _runner,
            _screens,
            () => "adb",
            Path.Combine(_root, "staging"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureAsync("serial"));

        Assert.Contains("unauthorized", exception.Message, StringComparison.Ordinal);
        Assert.Contains("authorized", exception.Message, StringComparison.Ordinal);
        var capture = Assert.Single(_runner.Requests, request => request.Arguments.Contains("screencap"));
        var cleanup = Assert.Single(_runner.Requests, request => request.Arguments.Contains("rm"));
        Assert.Equal(capture.Arguments[^1], cleanup.Arguments[^1]);
    }

    [Fact]
    public async Task CaptureAsync_SanitizesTheRemoteFileName()
    {
        _runner.Handler = WritePulledFile;
        using var service = new AdbThumbnailService(
            _runner,
            _screens,
            () => "adb",
            Path.Combine(_root, "staging"));

        await service.CaptureAsync("192.168.1.10:5555");

        var pull = Assert.Single(_runner.Requests, request => request.Arguments.Contains("pull"));
        Assert.StartsWith(
            "/sdcard/DroidDeck/thumb-192-168-1-10-5555-",
            pull.Arguments[3],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SkipsMirroredDevicesAndStopsOnCancellation()
    {
        _screens.Sessions.Add(new ScreenSessionInfo { Serial = "mirroring" });
        _runner.Handler = WritePulledFile;
        using var service = new AdbThumbnailService(
            _runner,
            _screens,
            () => "adb",
            Path.Combine(_root, "staging"));
        using var cancellation = new CancellationTokenSource();
        var captured = new List<string>();

        await service.RunAsync(
            ["idle", "mirroring"],
            TimeSpan.FromMilliseconds(20),
            thumbnail =>
            {
                captured.Add(thumbnail.Serial);
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(["idle"], captured);
    }

    [Fact]
    public async Task RunAsync_ContinuesAfterOneDeviceFails()
    {
        _runner.Handler = request =>
            request.Arguments.Contains("first") && request.Arguments.Contains("screencap")
                ? FakeProcessRunner.Failure(request, "failure")
                : WritePulledFile(request);
        using var service = new AdbThumbnailService(
            _runner,
            _screens,
            () => "adb",
            Path.Combine(_root, "staging"));
        using var cancellation = new CancellationTokenSource();
        var captured = new List<string>();

        await service.RunAsync(
            ["first", "second"],
            TimeSpan.FromMilliseconds(20),
            thumbnail =>
            {
                captured.Add(thumbnail.Serial);
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            cancellation.Token);

        Assert.Equal(["second"], captured);
    }

    private static DroidDeck.Core.Processes.ProcessResult WritePulledFile(
        DroidDeck.Core.Processes.ProcessRequest request)
    {
        if (request.Arguments.Contains("pull"))
        {
            File.WriteAllBytes(request.Arguments[^1], Png);
        }

        return FakeProcessRunner.Success(request);
    }
}
