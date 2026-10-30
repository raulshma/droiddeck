using Xunit;

namespace DroidDeck.ReleaseCheck.Tests;

public sealed class SoakCheckTests
{
    [Fact]
    public void EightHourSoakIsReleaseEligible()
    {
        Assert.True(SoakCheck.IsReleaseEligible(TimeSpan.FromHours(8)));
    }

    [Fact]
    public void ShortSoakIsNotReleaseEligible()
    {
        Assert.False(SoakCheck.IsReleaseEligible(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1)));
    }
}
