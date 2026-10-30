using Xunit;

namespace DroidDeck.ReleaseCheck.Tests;

public sealed class SoakAnalyzerTests
{
    private const long BaselineBytes = 350L * 1024 * 1024;
    private const int BaselineHandles = 500;
    private const double Warmup = 0;

    [Fact]
    public void StableMemoryAndHandlesPass()
    {
        var samples = BuildSamples(count: 60, memoryGrowthPercent: 0, handleGrowth: 0);

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.True(summary.Passed);
        Assert.Equal(0, summary.MemoryGrowthPercent, precision: 3);
        Assert.Equal(0, summary.HandleGrowth, precision: 3);
    }

    [Fact]
    public void MemoryGrowthOverBudgetFails()
    {
        var samples = BuildSamples(count: 60, memoryGrowthPercent: 20, handleGrowth: 0);

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.False(summary.Passed);
        Assert.Contains("working set grew", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MemoryGrowthAtBudgetFails()
    {
        var samples = new SoakSample[6];
        for (var index = 0; index < samples.Length; index++)
        {
            var elapsed = index * 10d;
            samples[index] = new SoakSample(
                DateTimeOffset.UnixEpoch.AddSeconds(elapsed),
                elapsed,
                index < 3 ? 100L : 105L,
                BaselineHandles);
        }

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.False(summary.Passed);
        Assert.Contains("at or over", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShrinkingMemoryPasses()
    {
        var samples = BuildSamples(count: 60, memoryGrowthPercent: -20, handleGrowth: 0);

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.True(summary.Passed);
    }

    [Fact]
    public void HandleGrowthOverBudgetFails()
    {
        var samples = BuildSamples(count: 60, memoryGrowthPercent: 0, handleGrowth: 100);

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.False(summary.Passed);
        Assert.Contains("handle count grew", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandleNoiseWithinBudgetPasses()
    {
        var samples = BuildSamples(count: 60, memoryGrowthPercent: 0, handleGrowth: 10);

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.True(summary.Passed);
    }

    [Fact]
    public void GrowthDuringWarmupIsIgnored()
    {
        var samples = new List<SoakSample>();
        for (var index = 0; index < 120; index++)
        {
            var elapsed = index * 10d;
            var workingSet = elapsed < 60 ? BaselineBytes + (index * 5L * 1024 * 1024) : BaselineBytes * 2;
            samples.Add(new SoakSample(
                DateTimeOffset.UnixEpoch.AddSeconds(elapsed),
                elapsed,
                workingSet,
                BaselineHandles));
        }

        var summary = SoakAnalyzer.Analyze(
            samples,
            warmupSeconds: 60,
            maxMemoryGrowthPercent: 5,
            maxHandleGrowth: 32);

        Assert.True(summary.Passed);
        Assert.Equal(0, summary.MemoryGrowthPercent, precision: 3);
    }

    [Fact]
    public void TooFewSamplesAfterWarmupFails()
    {
        var samples = BuildSamples(count: 3, memoryGrowthPercent: 0, handleGrowth: 0);

        var summary = SoakAnalyzer.Analyze(samples, Warmup, maxMemoryGrowthPercent: 5, maxHandleGrowth: 32);

        Assert.False(summary.Passed);
        Assert.Contains("at least 6", summary.Failures[0], StringComparison.Ordinal);
    }

    private static SoakSample[] BuildSamples(
        int count,
        double memoryGrowthPercent,
        int handleGrowth)
    {
        var samples = new SoakSample[count];
        for (var index = 0; index < count; index++)
        {
            var progress = count == 1 ? 1 : index / (double)(count - 1);
            var elapsed = index * 10d;
            samples[index] = new SoakSample(
                DateTimeOffset.UnixEpoch.AddSeconds(elapsed),
                elapsed,
                (long)(BaselineBytes * (1 + (memoryGrowthPercent / 100 * progress))),
                (int)Math.Round(BaselineHandles + (handleGrowth * progress)));
        }

        return samples;
    }
}
