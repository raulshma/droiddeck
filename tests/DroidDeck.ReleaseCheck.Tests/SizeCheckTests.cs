using Xunit;

namespace DroidDeck.ReleaseCheck.Tests;

public sealed class SizeCheckTests
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void SizesWithinBudgetPass()
    {
        var result = SizeCheck.Evaluate(
            new SizeMeasurement(200 * Mib, 100 * Mib),
            maxExtractedBytes: 250 * Mib,
            maxZipBytes: 120 * Mib);

        Assert.True(result.Passed);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void ExtractedSizeOverBudgetFails()
    {
        var result = SizeCheck.Evaluate(
            new SizeMeasurement((250 * Mib) + 1, null),
            maxExtractedBytes: 250 * Mib,
            maxZipBytes: 120 * Mib);

        Assert.False(result.Passed);
        Assert.Contains("extracted size", result.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZipOverBudgetFails()
    {
        var result = SizeCheck.Evaluate(
            new SizeMeasurement(200 * Mib, (120 * Mib) + 1),
            maxExtractedBytes: 250 * Mib,
            maxZipBytes: 120 * Mib);

        Assert.False(result.Passed);
        Assert.Contains("zip size", result.Failures[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactBudgetBoundaryPasses()
    {
        var result = SizeCheck.Evaluate(
            new SizeMeasurement(250 * Mib, 120 * Mib),
            maxExtractedBytes: 250 * Mib,
            maxZipBytes: 120 * Mib);

        Assert.True(result.Passed);
    }

    [Fact]
    public void MissingZipIsAllowed()
    {
        var result = SizeCheck.Evaluate(
            new SizeMeasurement(1, null),
            maxExtractedBytes: 250 * Mib,
            maxZipBytes: 120 * Mib);

        Assert.True(result.Passed);
    }
}
