using Xunit;

namespace DroidDeck.ReleaseCheck.Tests;

public sealed class StatisticsTests
{
    [Theory]
    [InlineData(new double[] { 3 }, 3)]
    [InlineData(new double[] { 3, 1, 2 }, 2)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    public void MedianUsesTheMiddleValue(double[] values, double expected)
    {
        Assert.Equal(expected, Statistics.Median(values));
    }

    [Fact]
    public void PercentileInterpolates()
    {
        var values = Enumerable.Range(1, 100).Select(value => (double)value).ToArray();

        Assert.Equal(95.05, Statistics.Percentile(values, 95), precision: 6);
        Assert.Equal(50.5, Statistics.Percentile(values, 50), precision: 6);
    }

    [Fact]
    public void EmptyValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => Statistics.Median([]));
    }

    [Fact]
    public void OutOfRangePercentileIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Statistics.Percentile([1], 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => Statistics.Percentile([1], -1));
    }
}
