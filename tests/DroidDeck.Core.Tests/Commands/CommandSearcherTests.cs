using DroidDeck.Core.Commands;

namespace DroidDeck.Core.Tests.Commands;

public sealed class CommandSearcherTests
{
    private static readonly CommandDefinition[] TestCommands =
    [
        new("shell.run", "Run on selected devices", "Run", _ => Task.CompletedTask)
        {
            Keywords = ["deploy", "build", "launch"],
            DefaultShortcut = KeyGesture.Parse("F5"),
        },
        new("shell.stop", "Stop running sessions", "Sessions", _ => Task.CompletedTask)
        {
            Keywords = ["cancel", "abort"],
            DefaultShortcut = KeyGesture.Parse("Shift+F5"),
        },
        new("shell.screenshot", "Take screenshot", "Screen", _ => Task.CompletedTask)
        {
            Keywords = ["capture", "snap"],
        },
        new("nav.settings", "Go to settings", "Navigate", _ => Task.CompletedTask)
        {
            Keywords = ["preferences", "options"],
        },
        new("nav.devices", "Go to devices", "Navigate", _ => Task.CompletedTask)
        {
            Keywords = ["phone", "adb"],
        },
    ];

    [Fact]
    public void EmptyQueryReturnsEveryCommandInRegistrationOrder()
    {
        var results = CommandSearcher.Search(TestCommands, string.Empty);

        Assert.Equal(TestCommands, results.Select(result => result.Command));
    }

    [Fact]
    public void FindsCommandsByTitle()
    {
        var results = CommandSearcher.Search(TestCommands, "screenshot");

        Assert.Equal("shell.screenshot", Assert.Single(results).Command.Id);
    }

    [Fact]
    public void FindsCommandsBySynonym()
    {
        var results = CommandSearcher.Search(TestCommands, "abort");

        Assert.Equal("shell.stop", Assert.Single(results).Command.Id);
    }

    [Fact]
    public void FindsCommandsByFuzzyPrefix()
    {
        var results = CommandSearcher.Search(TestCommands, "tak");

        var result = Assert.Single(results);
        Assert.Equal("shell.screenshot", result.Command.Id);
        Assert.True(result.Score > 0);
    }

    [Fact]
    public void FindsCommandsByTypoTolerantSubsequence()
    {
        var results = CommandSearcher.Search(TestCommands, "sttings");

        Assert.Equal("nav.settings", results[0].Command.Id);
    }

    [Fact]
    public void TitlesRankAboveKeywordAndScatteredMatches()
    {
        var results = CommandSearcher.Search(TestCommands, "run");

        Assert.True(results.Count >= 2);
        Assert.Equal("shell.run", results[0].Command.Id);
        Assert.True(
            results[0].Score > results[1].Score,
            $"title match {results[0].Score} should outrank the weaker match {results[1].Score}");
    }

    [Fact]
    public void DisabledCommandsAreExcludedFromResults()
    {
        var disabled = TestCommands[0] with { IsEnabledPredicate = () => false };

        var results = CommandSearcher.Search([disabled, .. TestCommands[1..]], "run");

        Assert.DoesNotContain(results, result => result.Command.Id == "shell.run");
    }

    [Fact]
    public void QueriesWithoutAnyMatchReturnAnEmptyResult()
    {
        Assert.Empty(CommandSearcher.Search(TestCommands, "zzzzzz"));
    }
}
