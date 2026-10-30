using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Covers the host path that creates plugin views for pages, project tabs, and
/// home widgets: timeout-isolated creation, health bookkeeping, and the
/// crash-guard wiring that pauses a plugin after repeated UI faults.
/// </summary>
public sealed class PluginUiIsolationTests
{
    private const string PluginId = "com.example.ui";

    [Fact]
    public async Task SuccessfulViewCreationReturnsContentAndRecordsHealth()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        var page = new TestPage("ui.page");

        var result = await invoker.CreateViewAsync(
            PluginId,
            "navigation page ui.page",
            token => page.CreateViewAsync(token));

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Canceled);
        Assert.Equal("view:ui.page", result.Content);
        Assert.Equal(1, health.Get(PluginId).SuccessCount);
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task ViewCreationFailureIsIsolatedAndMarksPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);

        var result = await invoker.CreateViewAsync(
            PluginId,
            "home widget ui.widget",
            _ => throw new InvalidOperationException("widget failed"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Content);
        Assert.Equal("widget failed", result.Error);
        var snapshot = health.Get(PluginId);
        Assert.False(snapshot.IsHealthy);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal("widget failed", snapshot.LastError);
    }

    [Fact]
    public async Task SlowViewCreationTimesOutInsteadOfBlockingTheHost()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));
        var neverCompletes = new TaskCompletionSource<object>();

        var result = await invoker.CreateViewAsync(
            PluginId,
            "navigation page ui.page",
            _ => neverCompletes.Task);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.False(result.Canceled);
        Assert.False(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task CallerCancellationIsReportedWithoutMarkingPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await invoker.CreateViewAsync(
            PluginId,
            "project tab ui.tab",
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new object();
            },
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.Canceled);
        Assert.False(result.TimedOut);
        Assert.True(health.Get(PluginId).IsHealthy);
        Assert.Equal(0, health.Get(PluginId).FailureCount);
    }

    [Fact]
    public async Task NullArgumentsAreRejected()
    {
        var invoker = new PluginActionInvoker(new PluginHealthTracker());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => invoker.CreateViewAsync(null!, "operation", _ => Task.FromResult<object>("view")));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => invoker.CreateViewAsync(PluginId, null!, _ => Task.FromResult<object>("view")));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => invoker.CreateViewAsync(PluginId, "operation", null!));
    }

    [Fact]
    public async Task ThreeReportedUiFaultsTripTheCrashGuard()
    {
        var health = new PluginHealthTracker();
        using var guard = new PluginCrashGuard(health);
        var tripped = new List<PluginCrashEventArgs>();
        guard.Tripped += (_, args) => tripped.Add(args);
        var reporter = new HealthRecordingReporter(health);
        var invoker = new PluginActionInvoker(health);

        for (var attempt = 0; attempt < PluginCrashGuard.DefaultThreshold; attempt++)
        {
            var result = await invoker.CreateViewAsync(
                PluginId,
                "home widget ui.widget",
                _ => throw new InvalidOperationException("widget failed"));
            if (!result.Succeeded && result.Error is not null)
            {
                reporter.Report(PluginId, "home widget ui.widget", new InvalidOperationException(result.Error));
            }
        }

        var crash = Assert.Single(tripped);
        Assert.Equal(PluginId, crash.PluginId);
        Assert.Equal(PluginCrashGuard.DefaultThreshold, crash.FailureCount);
        Assert.Equal("widget failed", crash.LastError);
        Assert.True(guard.IsTripped(PluginId));
    }

    [Fact]
    public async Task SuccessfulCreationBetweenFaultsPreventsTheCrashGuardTrip()
    {
        var health = new PluginHealthTracker();
        using var guard = new PluginCrashGuard(health);
        var tripped = 0;
        guard.Tripped += (_, _) => tripped++;
        var invoker = new PluginActionInvoker(health);
        var page = new TestPage("ui.page");

        await CreateFailingView(invoker);
        await CreateFailingView(invoker);
        await invoker.CreateViewAsync(PluginId, "navigation page", token => page.CreateViewAsync(token));
        await CreateFailingView(invoker);
        await CreateFailingView(invoker);

        Assert.Equal(0, tripped);
        Assert.False(guard.IsTripped(PluginId));
    }

    private static async Task CreateFailingView(PluginActionInvoker invoker) =>
        await invoker.CreateViewAsync(
            PluginId,
            "navigation page ui.page",
            _ => throw new InvalidOperationException("page failed"));

    private sealed class HealthRecordingReporter(PluginHealthTracker health) : IPluginFaultReporter
    {
        public void Report(string pluginId, string operation, Exception exception) =>
            health.RecordFailure(pluginId, $"UI {operation} failed: {exception.Message}");
    }

    private sealed class TestPage(string id) : INavigationPage
    {
        public string Id => id;

        public string Title => "Test page";

        public string Icon => string.Empty;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>($"view:{id}");
    }
}
