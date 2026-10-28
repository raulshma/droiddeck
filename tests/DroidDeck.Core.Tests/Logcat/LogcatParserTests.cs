using DroidDeck.Core.Logs;

namespace DroidDeck.Core.Tests.Logcat;

public sealed class LogcatParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseLine_ParsesThreadTimeWithUid()
    {
        var entry = LogcatParser.ParseLine(
            "2026-10-28 10:11:12.345  10123  4567  1000 I ActivityManager: Start proc 4567:com.example/u0a12",
            Now);

        Assert.NotNull(entry);
        Assert.Equal(LogSource.Logcat, entry.Source);
        Assert.Equal("I", entry.Level);
        Assert.Equal("ActivityManager", entry.Tag);
        Assert.Equal(4567, entry.Pid);
        Assert.Equal(1000, entry.Tid);
        Assert.Equal(10123, entry.Uid);
        Assert.Equal("Start proc 4567:com.example/u0a12", entry.Message);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 11, 12, 345, TimeSpan.Zero), entry.Timestamp);
        Assert.False(entry.IsContinuation);
    }

    [Fact]
    public void ParseLine_ParsesThreadTimeWithoutUid()
    {
        var entry = LogcatParser.ParseLine(
            "10-28 10:11:12.345  4567  4568 E AndroidRuntime: FATAL EXCEPTION: main",
            Now);

        Assert.NotNull(entry);
        Assert.Equal("E", entry.Level);
        Assert.Equal("AndroidRuntime", entry.Tag);
        Assert.Equal(4567, entry.Pid);
        Assert.Null(entry.Uid);
        Assert.Equal("FATAL EXCEPTION: main", entry.Message);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 10, 11, 12, 345, TimeSpan.Zero), entry.Timestamp);
    }

    [Fact]
    public void ParseLine_RecognizesBufferMarker()
    {
        var entry = LogcatParser.ParseLine("--------- beginning of crash", Now);

        Assert.NotNull(entry);
        Assert.Equal("logcat", entry.Tag);
        Assert.Equal("crash", entry.Buffer);
        Assert.Equal(Now, entry.Timestamp);
    }

    [Fact]
    public void ParseLine_TreatsUnrecognizedLineAsContinuation()
    {
        var entry = LogcatParser.ParseLine("    at com.example.MainActivity.onCreate(MainActivity.kt:12)", Now);

        Assert.NotNull(entry);
        Assert.True(entry.IsContinuation);
        Assert.Equal("logcat", entry.Tag);
        Assert.Equal("    at com.example.MainActivity.onCreate(MainActivity.kt:12)", entry.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLine_ReturnsNullForBlankInput(string raw)
    {
        Assert.Null(LogcatParser.ParseLine(raw, Now));
    }

    [Fact]
    public void TryParseActivityManagerEvent_StripsBracketsAndQuotes()
    {
        var entry = new LogEntry
        {
            Tag = "am_crash",
            Message = "[1000,1234,\"com.example\",0,\"java.lang.IllegalStateException\",\"boom\",\"Main.kt\",42]",
        };

        var parsed = LogcatParser.TryParseActivityManagerEvent(entry, out var eventName, out var fields);

        Assert.True(parsed);
        Assert.Equal("am_crash", eventName);
        Assert.Equal(8, fields.Count);
        Assert.Equal("com.example", fields[2]);
        Assert.Equal("java.lang.IllegalStateException", fields[4]);
        Assert.Equal("42", fields[7]);
    }

    [Fact]
    public void TryParseActivityManagerEvent_RejectsUnrelatedTags()
    {
        var entry = new LogEntry { Tag = "ActivityManager", Message = "ANR in com.example" };

        Assert.False(LogcatParser.TryParseActivityManagerEvent(entry, out _, out _));
    }

    [Fact]
    public void BoundedLogBufferStore_DropsOldestEntriesAndCountsThem()
    {
        var buffer = new BoundedLogBufferStore(3);
        for (var index = 0; index < 5; index++)
        {
            buffer.Append(new LogEntry { Message = $"line-{index}" });
        }

        var snapshot = buffer.Snapshot();

        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer.Dropped);
        Assert.Equal(5, buffer.TotalObserved);
        Assert.Equal(["line-2", "line-3", "line-4"], snapshot.Select(entry => entry.Message));
        Assert.Equal([3L, 4L, 5L], snapshot.Select(entry => entry.Sequence));
    }
}
