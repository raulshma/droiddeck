using DroidDeck.Core.Logs;

namespace DroidDeck.Core.Tests.Logs;

public sealed class HighlightEngineTests
{
    [Fact]
    public void Match_ReturnsCrashRuleForFatalLine()
    {
        var engine = new HighlightEngine(HighlightRule.BuiltIn);
        var entry = new LogEntry
        {
            Raw = "10-28 12:00:00.000  1234  1234 E AndroidRuntime: FATAL EXCEPTION: main",
            Message = "FATAL EXCEPTION: main",
            Tag = "AndroidRuntime",
        };

        var rule = engine.Match(entry);

        Assert.NotNull(rule);
        Assert.Equal("fatal", rule.Id);
        Assert.True(rule.Bold);
    }

    [Fact]
    public void Match_DisabledRulesAreIgnored()
    {
        var rules = HighlightRule.BuiltIn.Select(rule => rule.Id == "fatal"
            ? new HighlightRule
            {
                Id = rule.Id,
                Name = rule.Name,
                Pattern = rule.Pattern,
                IsRegex = rule.IsRegex,
                Foreground = rule.Foreground,
                Bold = rule.Bold,
                IsBuiltIn = rule.IsBuiltIn,
                IsEnabled = false,
            }
            : rule);
        var engine = new HighlightEngine(rules);
        var entry = new LogEntry
        {
            Raw = "E/AndroidRuntime: FATAL EXCEPTION: main",
            Message = "FATAL EXCEPTION: main",
        };

        var rule = engine.Match(entry);

        Assert.NotNull(rule);
        Assert.NotEqual("fatal", rule.Id);
    }

    [Fact]
    public void Match_LiteralPatternsAreEscaped()
    {
        var engine = new HighlightEngine(
        [
            new HighlightRule { Id = "dot", Name = "Dot", Pattern = "a.b", IsRegex = false },
        ]);
        var literal = new LogEntry { Raw = "value a.b here", Message = "value a.b here" };
        var regexOnly = new LogEntry { Raw = "value axb here", Message = "value axb here" };

        Assert.NotNull(engine.Match(literal));
        Assert.Null(engine.Match(regexOnly));
    }

    [Fact]
    public void Constructor_InvalidRegexIsSkipped()
    {
        var engine = new HighlightEngine(
        [
            new HighlightRule { Id = "bad", Name = "Bad", Pattern = "([unclosed", IsRegex = true },
            new HighlightRule { Id = "good", Name = "Good", Pattern = "needle", IsRegex = false },
        ]);

        Assert.Single(engine.Rules);
        Assert.Equal("good", engine.Rules[0].Id);
    }

    [Fact]
    public void Match_FirstMatchingRuleWins()
    {
        var engine = new HighlightEngine(
        [
            new HighlightRule { Id = "first", Name = "First", Pattern = "error", IsRegex = false },
            new HighlightRule { Id = "second", Name = "Second", Pattern = "error:", IsRegex = false },
        ]);
        var entry = new LogEntry { Raw = "E/Tag: error: boom", Message = "error: boom" };

        var rule = engine.Match(entry);

        Assert.NotNull(rule);
        Assert.Equal("first", rule.Id);
    }

    [Fact]
    public void Match_NoMatchReturnsNull()
    {
        var engine = new HighlightEngine(HighlightRule.BuiltIn);
        var entry = new LogEntry { Raw = "D/Tag: all quiet", Message = "all quiet" };

        Assert.Null(engine.Match(entry));
    }

    [Fact]
    public void Match_ExplicitRuleIdWinsOverPatternOrder()
    {
        var engine = new HighlightEngine(
        [
            new HighlightRule { Id = "first", Name = "First", Pattern = "error", IsRegex = false },
            new HighlightRule { Id = "plugin.rule", Name = "Plugin", Pattern = "nothing-matches", IsRegex = false },
        ]);
        var entry = new LogEntry
        {
            Raw = "E/Tag: error: boom",
            Message = "error: boom",
            HighlightRuleId = "plugin.rule",
        };

        var rule = engine.Match(entry);

        Assert.NotNull(rule);
        Assert.Equal("plugin.rule", rule.Id);
    }

    [Fact]
    public void Match_UnknownRuleIdFallsBackToPatterns()
    {
        var engine = new HighlightEngine(HighlightRule.BuiltIn);
        var entry = new LogEntry
        {
            Raw = "E/AndroidRuntime: FATAL EXCEPTION: main",
            Message = "FATAL EXCEPTION: main",
            HighlightRuleId = "plugin.missing",
        };

        var rule = engine.Match(entry);

        Assert.NotNull(rule);
        Assert.Equal("fatal", rule.Id);
    }

    [Fact]
    public void Match_DisabledRuleIdFallsBackToPatterns()
    {
        var engine = new HighlightEngine(
        [
            new HighlightRule { Id = "plugin.rule", Name = "Plugin", Pattern = "needle", IsRegex = false, IsEnabled = false },
            new HighlightRule { Id = "error", Name = "Errors", Pattern = "error", IsRegex = false },
        ]);
        var entry = new LogEntry
        {
            Raw = "E/Tag: error: boom",
            Message = "error: boom",
            HighlightRuleId = "plugin.rule",
        };

        var rule = engine.Match(entry);

        Assert.NotNull(rule);
        Assert.Equal("error", rule.Id);
    }
}
