using DroidDeck.Core.Commands;

namespace DroidDeck.Core.Tests.Commands;

public sealed class FuzzyMatcherTests
{
    [Fact]
    public void FindsCommandsByExactName()
    {
        var match = FuzzyMatcher.Match("run", "Run on selected devices");

        Assert.NotNull(match);
        Assert.True(match!.Score > 0);
        Assert.Equal([0, 1, 2], match.MatchedIndices);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var lower = FuzzyMatcher.Match("stop", "Stop running sessions");
        var upper = FuzzyMatcher.Match("STOP", "Stop running sessions");

        Assert.NotNull(lower);
        Assert.NotNull(upper);
        Assert.Equal(lower!.Score, upper!.Score);
    }

    [Fact]
    public void EmptyQueryMatchesEverythingWithANeutralScore()
    {
        var match = FuzzyMatcher.Match(string.Empty, "Anything at all");

        Assert.NotNull(match);
        Assert.Equal(0, match!.Score);
        Assert.Empty(match.MatchedIndices);
    }

    [Fact]
    public void NonMatchReturnsNull()
    {
        Assert.Null(FuzzyMatcher.Match("xyz", "Run on selected devices"));
        Assert.Null(FuzzyMatcher.Match("screenshot", "Stop"));
    }

    [Fact]
    public void QueryLongerThanCandidateNeverMatches()
    {
        Assert.Null(FuzzyMatcher.Match("running sessions now", "Stop"));
    }

    [Fact]
    public void RanksExactAbovePrefixAboveScattered()
    {
        var exact = FuzzyMatcher.Match("run", "Run")!.Score;
        var prefix = FuzzyMatcher.Match("run", "Run on selected devices")!.Score;
        var scattered = FuzzyMatcher.Match("run", "Refresh And Undo Mirage")!.Score;

        Assert.True(exact > prefix, $"exact {exact} should beat prefix {prefix}");
        Assert.True(prefix > scattered, $"prefix {prefix} should beat scattered {scattered}");
    }

    [Fact]
    public void RanksWordStartMatchesAboveMidWordMatches()
    {
        var wordStart = FuzzyMatcher.Match("rc", "Record screen")!.Score;
        var midWord = FuzzyMatcher.Match("rc", "Screen record")!.Score;

        Assert.True(wordStart > midWord, $"word start {wordStart} should beat mid word {midWord}");
    }

    [Fact]
    public void RanksContiguousRunsAboveGappySubsequences()
    {
        var contiguous = FuzzyMatcher.Match("mirr", "Start mirror")!.Score;
        var gappy = FuzzyMatcher.Match("mirr", "Manage In-app Reporting Rollups")!.Score;

        Assert.True(contiguous > gappy, $"contiguous {contiguous} should beat gappy {gappy}");
    }

    [Fact]
    public void SurvivesTyposByMatchingTheRemainingSubsequence()
    {
        var match = FuzzyMatcher.Match("sttings", "Settings");

        Assert.NotNull(match);
        Assert.Equal([0, 2, 3, 4, 5, 6, 7], match!.MatchedIndices);
    }

    [Fact]
    public void ReportsMatchSpansForHighlighting()
    {
        var match = FuzzyMatcher.Match("scr", "Take screenshot");

        Assert.NotNull(match);
        Assert.Equal([5, 6, 7], match!.MatchedIndices);
    }
}
