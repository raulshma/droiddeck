using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Runs;

public sealed class FlutterRunSessionTests
{
    [Fact]
    public async Task StartAsync_WaitsForTheAppToStart()
    {
        await using var session = CreateSession();
        await session.StartAsync();

        Assert.True(session.IsRunning);
        Assert.Equal("flutter-test-app", session.AppId);
    }

    [Fact]
    public async Task HotReloadAsync_ReportsTheReloadResult()
    {
        await using var session = CreateSession();
        await session.StartAsync();

        var result = await session.HotReloadAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Reloaded", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task HotRestartAsync_ReportsTheRestartResult()
    {
        await using var session = CreateSession();
        await session.StartAsync();

        var result = await session.HotRestartAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("Restarted", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task HotReloadAsync_SurfacesCompilationFailures()
    {
        await using var session = CreateSession("--fail-reload");
        await session.StartAsync();

        var result = await session.HotReloadAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("compilation error", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsync_TerminatesTheDaemon()
    {
        var session = CreateSession();
        await session.StartAsync();

        await session.StopAsync();

        Assert.False(session.IsRunning);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_ThrowsWhenTheDaemonExitsBeforeTheAppStarts()
    {
        await using var session = CreateSession("--fail-start");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Contains("pubspec.yaml", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task HotReloadAsync_BeforeStartThrows()
    {
        await using var session = CreateSession();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.HotReloadAsync());
    }

    [Fact]
    public async Task Sessions_ForwardDaemonOutputLines()
    {
        var lines = new List<ProcessOutputLine>();
        await using var session = new FlutterRunSession(
            CreateDaemonRequest(),
            line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            },
            startTimeout: TimeSpan.FromSeconds(30));
        await session.StartAsync();

        await session.HotReloadAsync();

        lock (lines)
        {
            Assert.Contains(
                lines,
                line => line.Text.Contains("Performing hot reload", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static FlutterRunSession CreateSession(params string[] arguments) =>
        new(CreateDaemonRequest(arguments), startTimeout: TimeSpan.FromSeconds(30));

    private static ProcessRequest CreateDaemonRequest(params string[] arguments) => new()
    {
        FileName = "dotnet",
        Arguments = [TestProcessHelper.AssemblyPath, "flutter-daemon", .. arguments],
    };
}
