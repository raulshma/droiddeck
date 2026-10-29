using DroidDeck.Core.Performance;

namespace DroidDeck.Core.Tests.Performance;

public sealed class PerformanceSampleParserTests
{
    [Fact]
    public void ParseCpuPercent_PrefersTheApplicationLine()
    {
        const string output = """
            Load: 1.42 / 1.10 / 0.94
            CPU usage from 62000ms to 0ms ago (2026-10-29 09:12:44.120):
              12.5% 4211/com.example.app: 7.1% user + 5.4% kernel
              3.0% 912/system_server: 1.8% user + 1.2% kernel
            TOTAL: 18% user + 9% kernel + 1% iowait + 0% irq
            """;

        Assert.Equal(12.5, PerformanceSampleParser.ParseCpuPercent(output, "com.example.app"));
    }

    [Fact]
    public void ParseCpuPercent_FallsBackToTotal()
    {
        const string output = """
             12.5% 4211/com.example.app: 7.1% user + 5.4% kernel
            TOTAL: 18% user + 9% kernel + 1% iowait + 0% irq
            """;

        Assert.Equal(28, PerformanceSampleParser.ParseCpuPercent(output, "com.other.app"));
        Assert.Equal(28, PerformanceSampleParser.ParseCpuPercent(output));
    }

    [Fact]
    public void ParseCpuPercent_ReturnsNullWithoutData()
    {
        Assert.Null(PerformanceSampleParser.ParseCpuPercent(string.Empty));
        Assert.Null(PerformanceSampleParser.ParseCpuPercent("Load: 1.0 / 0.8 / 0.6"));
    }

    [Fact]
    public void ParseMemoryMegabytes_ReadsTotalPss()
    {
        const string output = """
            Applications Memory Usage (in Kilobytes):
            Uptime: 123456 Realtime: 123456

            ** MEMINFO in pid 4211 [com.example.app] **
                               Pss      Pss   Shared  Private  Shared  Private     Swap      SwapPss     Heap      Heap      Heap
                             Total    Clean   Dirty    Dirty   Clean    Dirty   Total     Dirty     Size     Alloc      Free
                            ------  ------  ------  ------  ------  ------  ------  ------  ------  ------  ------
              Native Heap    12000       0    1200    10800       0       0       0         0    16384     9200     7184
              Dalvik Heap     8000       0     800     7200       0       0       0         0    12288     7000     5288
                    TOTAL    24576       0    2000    18000       0       0       0         0    28672    16200    12472

            TOTAL PSS:   24576
            """;

        Assert.Equal(24, PerformanceSampleParser.ParseMemoryMegabytes(output));
    }

    [Fact]
    public void ParseMemoryMegabytes_FallsBackToTotalRowInKilobytes()
    {
        const string output = """
                       Pss      Pss   Shared  Private
                     Total    Clean   Dirty    Dirty
                    ------  ------  ------  ------
              Native     1024       0       0     1024
               TOTAL     4096       0       0     4096
            """;

        Assert.Equal(4, PerformanceSampleParser.ParseMemoryMegabytes(output));
    }

    [Fact]
    public void ParseMemoryMegabytes_ReturnsNullWithoutData()
    {
        Assert.Null(PerformanceSampleParser.ParseMemoryMegabytes("No process found"));
    }

    [Fact]
    public void ParseFrameStats_ReadsCumulativeCounters()
    {
        const string output = """
            Applications Graphics Acceleration Info:
            Uptime: 123456 Realtime: 123456

            ** Graphics info for pid 4211 [com.example.app] **

            Stats since: 1234567890ns
            Total frames rendered: 1200
            Janky frames: 48 (4.00%)
            Janky frames (legacy): 48 (4.00%)
            50th percentile: 8ms
            90th percentile: 12ms
            Number Missed Vsync: 3
            """;

        var stats = PerformanceSampleParser.ParseFrameStats(output);

        Assert.NotNull(stats);
        Assert.Equal(1200, stats.TotalFrames);
        Assert.Equal(48, stats.JankyFrames);
        Assert.Equal(4, stats.JankPercent);
    }

    [Fact]
    public void ParseFrameStats_ReturnsNullWithoutFrameCounters()
    {
        Assert.Null(PerformanceSampleParser.ParseFrameStats("No process found"));
    }

    [Fact]
    public void ParseBattery_ReadsLevelAndTemperatureInCelsius()
    {
        const string output = """
            Current Battery Service state:
              AC powered: false
              USB powered: true
              status: 2
              health: 2
              level: 85
              temperature: 320
              voltage: 4100
            """;

        var battery = PerformanceSampleParser.ParseBattery(output);

        Assert.Equal(85, battery.Level);
        Assert.Equal(32, battery.TemperatureCelsius);
    }

    [Fact]
    public void ParseBattery_ReturnsNullsWithoutData()
    {
        var battery = PerformanceSampleParser.ParseBattery("unknown");

        Assert.Null(battery.Level);
        Assert.Null(battery.TemperatureCelsius);
    }

    [Fact]
    public void ComputeFrameRates_UsesTheDeltaBetweenSamples()
    {
        var previous = new PerformanceFrameStats { TotalFrames = 1000, JankyFrames = 20 };
        var current = new PerformanceFrameStats { TotalFrames = 1120, JankyFrames = 32 };

        var rates = PerformanceSampleParser.ComputeFrameRates(
            previous,
            current,
            TimeSpan.FromSeconds(2));

        Assert.Equal(60, rates.Fps);
        Assert.Equal(10, rates.JankPercent);
    }

    [Fact]
    public void ComputeFrameRates_FallsBackToCumulativePercentageWithoutPrevious()
    {
        var current = new PerformanceFrameStats
        {
            TotalFrames = 200,
            JankyFrames = 10,
            JankPercent = 5,
        };

        var rates = PerformanceSampleParser.ComputeFrameRates(null, current, TimeSpan.Zero);

        Assert.Null(rates.Fps);
        Assert.Equal(5, rates.JankPercent);
    }
}
