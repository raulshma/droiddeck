using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginActionInvokerTests
{
    private const string PluginId = "com.example.plugin";
    private static readonly DeviceActionContext Context = new("device-1", "Pixel");

    [Fact]
    public async Task SuccessfulDeviceActionIsRecordedAsHealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        var action = new RecordingDeviceAction();

        var result = await invoker.InvokeDeviceActionAsync(
            new PluginExtension<IDeviceAction>(PluginId, action),
            Context);

        Assert.True(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Canceled);
        Assert.Null(result.Error);
        Assert.Equal(1, action.ExecuteCount);
        Assert.Equal(1, health.Get(PluginId).SuccessCount);
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task ToolbarActionFailureIsIsolatedAndMarksPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        var action = new ThrowingToolbarAction();

        var result = await invoker.InvokeToolbarActionAsync(
            new PluginExtension<IDeviceToolbarAction>(PluginId, action),
            Context);

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal("toolbar failed", result.Error);
        var snapshot = health.Get(PluginId);
        Assert.False(snapshot.IsHealthy);
        Assert.Equal(1, snapshot.FailureCount);
        Assert.Equal("toolbar failed", snapshot.LastError);
    }

    [Fact]
    public async Task TimeoutIsReportedAndMarksPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));
        var neverCompletes = new TaskCompletionSource();

        var result = await invoker.InvokeAsync(PluginId, _ => neverCompletes.Task);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Contains("timeout", result.Error, StringComparison.OrdinalIgnoreCase);
        var snapshot = health.Get(PluginId);
        Assert.True(snapshot.LastFailureTimedOut);
        Assert.False(snapshot.IsHealthy);
    }

    [Fact]
    public async Task SynchronouslyHangingPluginTimesOutWithoutBlockingTheCaller()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));
        using var release = new ManualResetEventSlim();

        var invoke = invoker.InvokeAsync(PluginId, _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            return Task.CompletedTask;
        });
        var result = await invoke.WaitAsync(TimeSpan.FromSeconds(2));
        release.Set();

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.False(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task CallerCancellationIsReportedWithoutMarkingPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await invoker.InvokeAsync(
            PluginId,
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.Canceled);
        Assert.False(result.TimedOut);
        Assert.True(health.Get(PluginId).IsHealthy);
        Assert.Equal(0, health.Get(PluginId).FailureCount);
    }

    [Fact]
    public async Task SuccessAfterFailureRestoresHealth()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        await invoker.InvokeAsync(PluginId, _ => throw new InvalidOperationException("boom"));
        Assert.False(health.Get(PluginId).IsHealthy);

        var result = await invoker.InvokeAsync(PluginId, _ => Task.CompletedTask);

        Assert.True(result.Succeeded);
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public void TabContentIsReturnedWhenCreationSucceeds()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);

        var result = invoker.CreateTabContent(
            new PluginExtension<IDeviceTabProvider>(PluginId, new TestTab()),
            new DeviceTabContext("device-1", "Pixel"));

        Assert.True(result.Succeeded);
        Assert.Equal("Pixel tab", result.Content);
        Assert.Equal(1, health.Get(PluginId).SuccessCount);
    }

    [Fact]
    public void TabCreationFailureIsIsolatedAndMarksPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);

        var result = invoker.CreateTabContent(
            new PluginExtension<IDeviceTabProvider>(PluginId, new ThrowingTab()),
            new DeviceTabContext("device-1", null));

        Assert.False(result.Succeeded);
        Assert.Null(result.Content);
        Assert.Equal("tab failed", result.Error);
        Assert.False(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public void NonPositiveTimeoutIsRejected()
    {
        var health = new PluginHealthTracker();

        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginActionInvoker(health, TimeSpan.Zero));
    }

    [Fact]
    public async Task NullExtensionIsRejected()
    {
        var invoker = new PluginActionInvoker(new PluginHealthTracker());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => invoker.InvokeDeviceActionAsync(null!, Context));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => invoker.InvokeToolbarActionAsync(null!, Context));
    }

    [Fact]
    public async Task AViewTimeoutKeepsTheTokenAliveForTheAbandonedPluginTask()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));
        Exception? observedByPlugin = null;

        var result = await invoker.CreateViewAsync(PluginId, "view", async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (Exception exception)
            {
                observedByPlugin = exception;
            }

            return "late";
        });

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        // The abandoned task must see its token canceled, not disposed.
        await Wait.UntilAsync(() => observedByPlugin is not null);
        Assert.True(
            observedByPlugin is OperationCanceledException,
            $"Expected a cancellation, got {observedByPlugin?.GetType().Name}");
    }

    [Fact]
    public async Task AnAbandonedViewTaskFaultIsObservedAndLogged()
    {
        var health = new PluginHealthTracker();
        var logger = new CapturingLogger();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50), logger: logger);

        var result = await invoker.CreateViewAsync(PluginId, "view", _ =>
            Task.Delay(TimeSpan.FromMilliseconds(200), CancellationToken.None)
                .ContinueWith<object>(_ => throw new InvalidOperationException("late fault"), TaskScheduler.Default));

        Assert.True(result.TimedOut);
        await Wait.UntilAsync(() => logger.Entries.Any(entry =>
            entry.Message.Contains("abandoned", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(
            logger.Entries,
            entry => entry.Message.Contains("abandoned", StringComparison.OrdinalIgnoreCase) &&
                entry.Exception?.GetBaseException() is InvalidOperationException);
    }

    [Fact]
    public async Task AnInvocationTimeoutKeepsTheTokenAliveForTheAbandonedPluginTask()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));
        Exception? observedByPlugin = null;

        var result = await invoker.InvokeAsync(PluginId, async token =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (Exception exception)
            {
                observedByPlugin = exception;
            }
        });

        Assert.True(result.TimedOut);
        await Wait.UntilAsync(() => observedByPlugin is not null);
        Assert.True(
            observedByPlugin is OperationCanceledException,
            $"Expected a cancellation, got {observedByPlugin?.GetType().Name}");
    }

    [Fact]
    public async Task AUserCallbackMayOutliveTheTimeoutWithoutFailing()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));

        var result = await invoker.InvokeUiCallbackAsync(
            PluginId,
            // A dialog the user keeps open ignores the callback's token; the
            // delay here does the same on purpose.
            token => Task.Delay(TimeSpan.FromMilliseconds(150), CancellationToken.None));

        // A dialog the user keeps open must not surface as a false timeout.
        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.TimedOut);
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task AUserCallbackThatStaysPendingIsReportedThroughTheWatchdog()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var invoke = invoker.InvokeUiCallbackAsync(PluginId, _ => completion.Task);
        await Wait.UntilAsync(() => !health.Get(PluginId).IsHealthy);
        Assert.True(health.Get(PluginId).LastFailureTimedOut);

        completion.TrySetResult();
        var result = await invoke;
        Assert.True(result.Succeeded, result.Error);
        // The final success restores health after the transient watchdog fault.
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task AUserCallbackExceptionIsIsolated()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);

        var result = await invoker.InvokeUiCallbackAsync(
            PluginId,
            _ => throw new InvalidOperationException("click failed"));

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal("click failed", result.Error);
        Assert.False(health.Get(PluginId).IsHealthy);
    }

    private sealed class CapturingLogger : ILogger<PluginActionInvoker>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }

    private sealed class RecordingDeviceAction : IDeviceAction
    {
        public int ExecuteCount { get; private set; }

        public string Id => "test.deviceAction";

        public string Name => "Test device action";

        public bool RequiresConfirmation => false;

        public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
        {
            Assert.Same(Context, context);
            ExecuteCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingToolbarAction : IDeviceToolbarAction
    {
        public string Id => "test.toolbarAction";

        public string Title => "Throwing toolbar action";

        public string? ToolTip => null;

        public int Order => 0;

        public bool RequiresConfirmation => false;

        public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("toolbar failed");
    }

    private sealed class TestTab : IDeviceTabProvider
    {
        public string Id => "test.tab";

        public string Title => "Test tab";

        public int Order => 0;

        public object CreateContent(DeviceTabContext context) => $"{context.Model} tab";
    }

    private sealed class ThrowingTab : IDeviceTabProvider
    {
        public string Id => "test.throwingTab";

        public string Title => "Throwing tab";

        public int Order => 0;

        public object CreateContent(DeviceTabContext context)
            => throw new InvalidOperationException("tab failed");
    }
}
