using System.Diagnostics;
using DroidDeck.Core.Logs;

namespace DroidDeck.Core.Tests.Logcat;

/// <summary>
/// Replay harness for the logcat pipeline. The default run replays 100,000 lines
/// through the parse and buffer path and requires at least 10,000 lines per second.
/// The ten-minute soak from the acceptance criteria runs with the "Soak" trait:
/// <c>dotnet test --filter Trait=Category=Soak</c>.
/// </summary>
public sealed class LogcatReplayHarnessTests
{
    private const int BufferCapacity = 20_000;
    private const int FastLineCount = 100_000;
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Replay_SustainsAtLeastTenThousandLinesPerSecond()
    {
        var (elapsed, parsed, dropped) = Replay(FastLineCount);

        Assert.Equal(FastLineCount - BufferCapacity, dropped);
        Assert.Equal(BufferCapacity, parsed - dropped);
        var linesPerSecond = parsed / elapsed.TotalSeconds;
        Assert.True(
            linesPerSecond >= 10_000,
            $"Expected at least 10,000 lines/s but replayed {linesPerSecond:N0} lines/s " +
            $"for {parsed:N0} lines in {elapsed.TotalSeconds:F2}s.");
    }

    [Fact]
    [Trait("Category", "Soak")]
    public void Replay_SustainsRateForTenMinuteSoak()
    {
        const int lines = 10_000 * 600;
        var (elapsed, parsed, dropped) = Replay(lines);

        Assert.Equal(lines - BufferCapacity, dropped);
        Assert.True(
            parsed / elapsed.TotalSeconds >= 10_000,
            $"Soak replayed {parsed / elapsed.TotalSeconds:N0} lines/s, below the 10,000 lines/s budget.");
    }

    private static (TimeSpan Elapsed, int Parsed, int Dropped) Replay(int lineCount)
    {
        var store = new BoundedLogBufferStore(BufferCapacity);
        var stopwatch = Stopwatch.StartNew();
        var parsed = 0;
        for (var index = 0; index < lineCount; index++)
        {
            var raw =
                $"10-28 12:00:00.000  1234  5678 D ActivityTaskManager: replay line {index} " +
                "with some payload text so parsing does real work";
            var entry = LogcatParser.ParseLine(raw, Now);
            if (entry is not null)
            {
                store.Append(entry);
                parsed++;
            }
        }

        stopwatch.Stop();
        Assert.Equal(BufferCapacity, store.Count);
        return (stopwatch.Elapsed, parsed, store.Dropped);
    }
}
