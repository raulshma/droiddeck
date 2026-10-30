using Xunit;

namespace DroidDeck.ReleaseCheck.Tests;

public sealed class StartupAnalyzerTests
{
    [Fact]
    public void SamplesWithinBudgetPass()
    {
        var summary = StartupAnalyzer.Analyze(
            [900, 1_000, 1_100, 1_200, 1_300],
            [],
            [],
            runs: 6,
            warmupRuns: 1,
            medianBudgetMilliseconds: 2_000,
            p95BudgetMilliseconds: 3_000);

        Assert.True(summary.Passed);
        Assert.Equal(1_100, summary.MedianMilliseconds, precision: 3);
    }

    [Fact]
    public void MedianOverBudgetFails()
    {
        var summary = StartupAnalyzer.Analyze(
            [2_500, 2_600, 2_700],
            [],
            [],
            runs: 3,
            warmupRuns: 0,
            medianBudgetMilliseconds: 2_000,
            p95BudgetMilliseconds: 3_000);

        Assert.False(summary.Passed);
        Assert.Contains("median startup", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void P95OverBudgetFails()
    {
        var summary = StartupAnalyzer.Analyze(
            [1_000, 1_000, 1_000, 5_000],
            [],
            [],
            runs: 4,
            warmupRuns: 0,
            medianBudgetMilliseconds: 2_000,
            p95BudgetMilliseconds: 3_000);

        Assert.False(summary.Passed);
        Assert.Contains("p95 startup", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TimedOutRunFails()
    {
        var summary = StartupAnalyzer.Analyze(
            [1_000, 1_000],
            [3],
            [],
            runs: 3,
            warmupRuns: 0,
            medianBudgetMilliseconds: 2_000,
            p95BudgetMilliseconds: 3_000);

        Assert.False(summary.Passed);
        Assert.Contains("did not show the main window", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSamplesFail()
    {
        var summary = StartupAnalyzer.Analyze(
            [],
            [],
            [],
            runs: 1,
            warmupRuns: 1,
            medianBudgetMilliseconds: 2_000,
            p95BudgetMilliseconds: 3_000);

        Assert.False(summary.Passed);
        Assert.Contains("No startup samples", summary.Failures[0], StringComparison.Ordinal);
    }

    [Fact]
    public void StartupFailureWindowFails()
    {
        var summary = StartupAnalyzer.Analyze(
            [500, 600],
            [],
            [1],
            runs: 3,
            warmupRuns: 0,
            medianBudgetMilliseconds: 2_000,
            p95BudgetMilliseconds: 3_000);

        Assert.False(summary.Passed);
        Assert.Contains("startup failure window", summary.Failures[0], StringComparison.OrdinalIgnoreCase);
    }
}
