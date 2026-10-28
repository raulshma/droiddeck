using DroidDeck.Core.Resources;

namespace DroidDeck.Core.Tests.Resources;

public sealed class ResourceGuardTests
{
    [Fact]
    public async Task TryAcquire_EnforcesLimitAndExplainNamesConsumers()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxMirrors = 1 });

        Assert.True(guard.TryAcquire(ResourceKind.Mirror, "17bd753c", out var first));
        Assert.NotNull(first);
        Assert.False(guard.TryAcquire(ResourceKind.Mirror, "emulator-5554", out var second));
        Assert.Null(second);

        var explanation = guard.Explain();
        Assert.Contains("17bd753c", explanation, StringComparison.Ordinal);
        Assert.Contains("1 mirror", explanation, StringComparison.Ordinal);

        await first.DisposeAsync();
        Assert.True(guard.TryAcquire(ResourceKind.Mirror, "emulator-5554", out var replacement));
        await replacement!.DisposeAsync();
    }

    [Fact]
    public async Task UpdateLimits_ShrinkingLimitAppliesImmediately()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxMirrors = 3 });
        Assert.True(guard.TryAcquire(ResourceKind.Mirror, "first", out var first));

        guard.UpdateLimits(new ResourceLimits { MaxMirrors = 1 });

        Assert.False(guard.TryAcquire(ResourceKind.Mirror, "second", out _));
        await first!.DisposeAsync();
        Assert.True(guard.TryAcquire(ResourceKind.Mirror, "second", out var second));
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilALeaseIsReleased()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxBuilds = 1 });
        var first = await guard.AcquireAsync(ResourceKind.Build, "first");
        var waiting = guard.AcquireAsync(ResourceKind.Build, "second");

        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        await first.DisposeAsync();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await second.DisposeAsync();
    }

    [Fact]
    public async Task UpdateLimits_GrowingLimitWakesWaiters()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxRecordings = 1 });
        var first = await guard.AcquireAsync(ResourceKind.Recording, "first");
        var waiting = guard.AcquireAsync(ResourceKind.Recording, "second");

        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        guard.UpdateLimits(new ResourceLimits { MaxRecordings = 2 });
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        await first.DisposeAsync();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_RaisesWarningAtThresholdAndLimit()
    {
        using var guard = new ResourceGuard(new ResourceLimits
        {
            MaxMirrors = 2,
            WarnAtPercent = 50,
        });
        var warnings = new List<ResourceWarningEventArgs>();
        guard.WarningRaised += (_, args) => warnings.Add(args);

        var first = guard.TryAcquire(ResourceKind.Mirror, "one", out var firstLease);
        var second = guard.TryAcquire(ResourceKind.Mirror, "two", out var secondLease);

        Assert.True(first);
        Assert.True(second);
        Assert.Contains(warnings, warning => warning.Count == 1 && warning.Message.Contains("approaching", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Count == 2 && warning.Message.Contains("limit reached", StringComparison.Ordinal));
        Assert.Contains("one", warnings[^1].Message, StringComparison.Ordinal);
        Assert.Contains("two", warnings[^1].Message, StringComparison.Ordinal);

        await firstLease!.DisposeAsync();
        await secondLease!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquire_WarnsBeforeTheLimitAtTheDefaultPercentage()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxMirrors = 3 });
        var warnings = new List<ResourceWarningEventArgs>();
        guard.WarningRaised += (_, args) => warnings.Add(args);

        var first = await guard.AcquireAsync(ResourceKind.Mirror, "one");
        Assert.Empty(warnings);

        var second = await guard.AcquireAsync(ResourceKind.Mirror, "two");
        Assert.Single(warnings);

        var third = await guard.AcquireAsync(ResourceKind.Mirror, "three");

        Assert.Contains(
            warnings,
            warning => warning.Count == 2 && warning.Message.Contains("approaching", StringComparison.Ordinal));
        Assert.Contains(
            warnings,
            warning => warning.Count == 3 && warning.Message.Contains("limit reached", StringComparison.Ordinal));

        await first.DisposeAsync();
        await second.DisposeAsync();
        await third.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_DoesNotWarnWhenASingleSlotCategoryIsUsedNormally()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxBuilds = 1 });
        var warnings = new List<ResourceWarningEventArgs>();
        guard.WarningRaised += (_, args) => warnings.Add(args);

        var lease = await guard.AcquireAsync(ResourceKind.Build, "app");

        Assert.Empty(warnings);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task AcquireAsync_WarnsOnceWhenASingleSlotCategoryBlocksASecondConsumer()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxBuilds = 1 });
        var warnings = new List<ResourceWarningEventArgs>();
        guard.WarningRaised += (_, args) => warnings.Add(args);
        var first = await guard.AcquireAsync(ResourceKind.Build, "app");

        var waiting = guard.AcquireAsync(ResourceKind.Build, "widget");

        var warning = Assert.Single(warnings);
        Assert.Equal(ResourceKind.Build, warning.Kind);
        Assert.Equal(1, warning.Count);
        Assert.Equal(1, warning.Limit);
        Assert.Contains("app", warning.Message, StringComparison.Ordinal);
        Assert.Contains("widget", warning.Message, StringComparison.Ordinal);

        await first.DisposeAsync();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(warnings);
        await second.DisposeAsync();
    }

    [Fact]
    public void TryAcquire_WarnsWhenASingleSlotCategoryRefusesASecondConsumer()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxBuilds = 1 });
        var warnings = new List<ResourceWarningEventArgs>();
        guard.WarningRaised += (_, args) => warnings.Add(args);
        Assert.True(guard.TryAcquire(ResourceKind.Build, "app", out _));

        Assert.False(guard.TryAcquire(ResourceKind.Build, "widget", out _));

        var warning = Assert.Single(warnings);
        Assert.Equal(1, warning.Limit);
        Assert.Contains("app", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcquireAsync_HonorsCancellationWhileWaiting()
    {
        using var guard = new ResourceGuard(new ResourceLimits { MaxMirrors = 1 });
        var lease = await guard.AcquireAsync(ResourceKind.Mirror, "first");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => guard.AcquireAsync(ResourceKind.Mirror, "second", cancellation.Token));

        await lease.DisposeAsync();
    }
}
