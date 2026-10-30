namespace DroidDeck.Plugins.Tests;

public sealed class PluginCrashGuardTests
{
    private const string PluginId = "com.example.sample";

    [Fact]
    public void ThreeConsecutiveFailuresTripTheGuardWithTheLastError()
    {
        var health = new PluginHealthTracker();
        using var guard = new PluginCrashGuard(health);
        var trips = new List<PluginCrashEventArgs>();
        guard.Tripped += (_, args) => trips.Add(args);

        health.RecordFailure(PluginId, "boom 1");
        health.RecordFailure(PluginId, "boom 2");
        Assert.Empty(trips);
        Assert.False(guard.IsTripped(PluginId));

        health.RecordFailure(PluginId, "boom 3", timedOut: true);

        var trip = Assert.Single(trips);
        Assert.Equal(PluginId, trip.PluginId);
        Assert.Equal(3, trip.FailureCount);
        Assert.Equal("boom 3", trip.LastError);
        Assert.True(trip.TimedOut);
        Assert.True(guard.IsTripped(PluginId));

        health.RecordFailure(PluginId, "boom 4");
        Assert.Single(trips);
    }

    [Fact]
    public void ASuccessResetsTheFailureRun()
    {
        var health = new PluginHealthTracker();
        using var guard = new PluginCrashGuard(health);
        var trips = new List<PluginCrashEventArgs>();
        guard.Tripped += (_, args) => trips.Add(args);

        health.RecordFailure(PluginId, "boom 1");
        health.RecordFailure(PluginId, "boom 2");
        health.RecordSuccess(PluginId);
        health.RecordFailure(PluginId, "boom 3");
        health.RecordFailure(PluginId, "boom 4");

        Assert.Empty(trips);
        Assert.False(guard.IsTripped(PluginId));
    }

    [Fact]
    public void ARecoveredPluginCanTripAgain()
    {
        var health = new PluginHealthTracker();
        using var guard = new PluginCrashGuard(health);
        var trips = new List<PluginCrashEventArgs>();
        guard.Tripped += (_, args) => trips.Add(args);

        health.RecordFailure(PluginId, "boom 1");
        health.RecordFailure(PluginId, "boom 2");
        health.RecordFailure(PluginId, "boom 3");
        health.RecordSuccess(PluginId);
        health.RecordFailure(PluginId, "boom 4");
        health.RecordFailure(PluginId, "boom 5");
        health.RecordFailure(PluginId, "boom 6");

        Assert.Equal(2, trips.Count);
        Assert.Equal("boom 3", trips[0].LastError);
        Assert.Equal("boom 6", trips[1].LastError);
    }

    [Fact]
    public void OtherPluginsAreNotAffected()
    {
        var health = new PluginHealthTracker();
        using var guard = new PluginCrashGuard(health);
        var trips = new List<PluginCrashEventArgs>();
        guard.Tripped += (_, args) => trips.Add(args);

        for (var index = 0; index < 3; index++)
        {
            health.RecordFailure("com.example.other", "boom");
        }

        Assert.DoesNotContain(trips, trip => trip.PluginId == PluginId);
        Assert.True(guard.IsTripped("com.example.other"));
    }

    [Fact]
    public void AThresholdBelowThreeIsRejected()
    {
        var health = new PluginHealthTracker();

        Assert.Throws<ArgumentOutOfRangeException>(() => { using var guard = new PluginCrashGuard(health, 1); });
    }
}
