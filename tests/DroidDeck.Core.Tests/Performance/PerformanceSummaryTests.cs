using DroidDeck.Core.Performance;

namespace DroidDeck.Core.Tests.Performance;

public sealed class PerformanceSummaryTests
{
    [Fact]
    public void FromSamples_ComputesDurationAndSeriesStatistics()
    {
        var start = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        var samples = new[]
        {
            new PerformanceSample
            {
                Timestamp = start,
                CpuPercent = 10,
                MemoryMegabytes = 100,
                Fps = 60,
                JankPercent = 2,
                BatteryPercent = 90,
            },
            new PerformanceSample
            {
                Timestamp = start.AddSeconds(2),
                CpuPercent = 30,
                MemoryMegabytes = 140,
                Fps = 40,
                JankPercent = 8,
                BatteryPercent = 88,
            },
        };

        var summary = PerformanceSummary.FromSamples(samples);

        Assert.Equal(2, summary.SampleCount);
        Assert.Equal(TimeSpan.FromSeconds(2), summary.Duration);
        Assert.Equal(10, summary.Cpu.Min);
        Assert.Equal(20, summary.Cpu.Average);
        Assert.Equal(30, summary.Cpu.Max);
        Assert.Equal(120, summary.Memory.Average);
        Assert.Equal(50, summary.Fps.Average);
        Assert.Equal(5, summary.Jank.Average);
        Assert.Equal(89, summary.Battery.Average);
        Assert.Equal(2, summary.Battery.Count);
    }

    [Fact]
    public void FromSamples_IgnoresMissingValues()
    {
        var start = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
        var summary = PerformanceSummary.FromSamples(
        [
            new PerformanceSample { Timestamp = start, CpuPercent = 10 },
            new PerformanceSample { Timestamp = start.AddSeconds(1), Fps = 55 },
        ]);

        Assert.Equal(1, summary.Cpu.Count);
        Assert.Equal(10, summary.Cpu.Average);
        Assert.Equal(0, summary.Memory.Count);
        Assert.Null(summary.Memory.Average);
        Assert.Equal(1, summary.Fps.Count);
    }

    [Fact]
    public void FromSamples_ReturnsAnEmptySummaryWithoutSamples()
    {
        var summary = PerformanceSummary.FromSamples([]);

        Assert.Equal(0, summary.SampleCount);
        Assert.Equal(TimeSpan.Zero, summary.Duration);
        Assert.Null(summary.Cpu.Average);
    }
}
