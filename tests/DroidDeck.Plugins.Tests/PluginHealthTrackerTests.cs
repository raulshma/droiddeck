namespace DroidDeck.Plugins.Tests;

public sealed class PluginHealthTrackerTests
{
    [Fact]
    public void UnknownPluginIsReportedHealthy()
    {
        var tracker = new PluginHealthTracker();

        var health = tracker.Get("com.example.plugin");

        Assert.True(health.IsHealthy);
        Assert.Equal("Healthy", health.StatusLabel);
        Assert.Equal(0, health.SuccessCount);
        Assert.Equal(0, health.FailureCount);
        Assert.Null(health.LastError);
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void FailureMarksPluginUnhealthyUntilTheNextSuccess()
    {
        var now = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var tracker = new PluginHealthTracker(new FixedTimeProvider(now));

        tracker.RecordFailure("com.example.plugin", "boom", timedOut: true);

        var failed = tracker.Get("com.example.plugin");
        Assert.False(failed.IsHealthy);
        Assert.Equal("Unhealthy", failed.StatusLabel);
        Assert.Equal(1, failed.FailureCount);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.Equal("boom", failed.LastError);
        Assert.Equal(now, failed.LastFailureAt);
        Assert.True(failed.LastFailureTimedOut);

        tracker.RecordSuccess("com.example.plugin");

        var recovered = tracker.Get("com.example.plugin");
        Assert.True(recovered.IsHealthy);
        Assert.Equal(1, recovered.SuccessCount);
        Assert.Equal(1, recovered.FailureCount);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.LastFailureAt);
        Assert.False(recovered.LastFailureTimedOut);
    }

    [Fact]
    public void ConsecutiveFailuresAccumulate()
    {
        var tracker = new PluginHealthTracker();

        tracker.RecordFailure("com.example.plugin", "first");
        tracker.RecordFailure("com.example.plugin", "second");

        var health = tracker.Get("com.example.plugin");
        Assert.Equal(2, health.FailureCount);
        Assert.Equal(2, health.ConsecutiveFailures);
        Assert.Equal("second", health.LastError);
    }

    [Fact]
    public void HealthChangedIsRaisedForEveryRecordedOutcome()
    {
        var tracker = new PluginHealthTracker();
        var changes = 0;
        tracker.HealthChanged += (_, _) => changes++;

        tracker.RecordSuccess("com.example.plugin");
        tracker.RecordFailure("com.example.plugin", "boom");

        Assert.Equal(2, changes);
    }

    [Fact]
    public void SnapshotReturnsEveryPluginOrderedById()
    {
        var tracker = new PluginHealthTracker();
        tracker.RecordFailure("com.example.zulu", "boom");
        tracker.RecordSuccess("com.example.alpha");

        var snapshot = tracker.Snapshot();

        Assert.Collection(
            snapshot,
            health => Assert.Equal("com.example.alpha", health.PluginId),
            health => Assert.Equal("com.example.zulu", health.PluginId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPluginIdsAreRejected(string? pluginId)
    {
        var tracker = new PluginHealthTracker();

        Assert.ThrowsAny<ArgumentException>(() => tracker.Get(pluginId!));
        Assert.ThrowsAny<ArgumentException>(() => tracker.RecordSuccess(pluginId!));
        Assert.ThrowsAny<ArgumentException>(() => tracker.RecordFailure(pluginId!, "boom"));
    }

    [Fact]
    public void NullFailureMessageIsRejected()
    {
        var tracker = new PluginHealthTracker();

        Assert.Throws<ArgumentNullException>(() => tracker.RecordFailure("com.example.plugin", null!));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
