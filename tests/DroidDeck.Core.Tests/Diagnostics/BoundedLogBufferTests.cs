using DroidDeck.Core.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Tests.Diagnostics;

public sealed class BoundedLogBufferTests
{
    [Fact]
    public void Add_KeepsOnlyTheConfiguredCapacity()
    {
        var buffer = new BoundedLogBuffer(3);

        for (var index = 0; index < 10; index++)
        {
            buffer.Add(Entry($"message-{index}"));
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal(7, buffer.DroppedCount);
        Assert.Equal(10, buffer.TotalWritten);
        Assert.Equal(
            ["message-7", "message-8", "message-9"],
            buffer.Snapshot().Select(entry => entry.Message));
    }

    [Fact]
    public void Snapshot_IsAnIndependentCopy()
    {
        var buffer = new BoundedLogBuffer(2);
        buffer.Add(Entry("first"));

        var snapshot = buffer.Snapshot();
        buffer.Add(Entry("second"));

        Assert.Single(snapshot);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void RecentErrors_ReturnsOnlyErrorsInChronologicalOrder()
    {
        var buffer = new BoundedLogBuffer(10);
        buffer.Add(Entry("info", LogLevel.Information));
        buffer.Add(Entry("error-1", LogLevel.Error));
        buffer.Add(Entry("warning", LogLevel.Warning));
        buffer.Add(Entry("error-2", LogLevel.Critical));

        var errors = buffer.RecentErrors(5);

        Assert.Equal(["error-1", "error-2"], errors.Select(entry => entry.Message));
    }

    [Fact]
    public void RecentErrors_LimitsToTheMostRecentEntries()
    {
        var buffer = new BoundedLogBuffer(10);
        for (var index = 0; index < 5; index++)
        {
            buffer.Add(Entry($"error-{index}", LogLevel.Error));
        }

        var errors = buffer.RecentErrors(2);

        Assert.Equal(["error-3", "error-4"], errors.Select(entry => entry.Message));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedLogBuffer(0));
    }

    [Fact]
    public void RecentErrors_RejectsNonPositiveLimit()
    {
        var buffer = new BoundedLogBuffer(2);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.RecentErrors(0));
    }

    private static LogEntry Entry(string message, LogLevel level = LogLevel.Information) =>
        new(DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            level,
            "Tests",
            message,
            SessionId: "SESSION");
}
