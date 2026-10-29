using DroidDeck.Core.Logs;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Logs;

public sealed class PluginLogEnricherTests
{
    [Fact]
    public void Enrich_AppliesParserFieldsAndHighlightRule()
    {
        var registry = new TestPluginRegistry();
        var parser = new StubParser("plugin.unity", priority: 0)
        {
            Result = new LogParserResult("Unity", "W", "Rewritten", "fatal"),
        };
        registry.AddLogParser(parser);
        using var enricher = new PluginLogEnricher(registry);
        var entry = CreateEntry();

        enricher.Enrich(entry, "emulator-5554");

        Assert.Equal("Unity", entry.Tag);
        Assert.Equal("W", entry.Level);
        Assert.Equal("Rewritten", entry.Message);
        Assert.Equal("fatal", entry.HighlightRuleId);
        var context = Assert.Single(parser.Contexts);
        Assert.Equal("emulator-5554", context.DeviceSerial);
        Assert.Equal("AndroidRuntime", context.Tag);
        Assert.Equal(entry.Raw, context.RawLine);
    }

    [Fact]
    public void Enrich_RunsParsersInPriorityOrderAndFirstRuleWins()
    {
        var registry = new TestPluginRegistry();
        var late = new StubParser("plugin.late", priority: 20)
        {
            Result = new LogParserResult(HighlightRuleId: "test-failure"),
        };
        var early = new StubParser("plugin.early", priority: 10)
        {
            Result = new LogParserResult("EarlyTag", HighlightRuleId: "fatal"),
        };
        registry.AddLogParser(late);
        registry.AddLogParser(early);
        using var enricher = new PluginLogEnricher(registry);
        var entry = CreateEntry();

        enricher.Enrich(entry, "serial");

        Assert.Equal("EarlyTag", entry.Tag);
        Assert.Equal("fatal", entry.HighlightRuleId);
        Assert.Equal("EarlyTag", Assert.Single(late.Contexts).Tag);
    }

    [Fact]
    public void Enrich_IsolatesParserFaults()
    {
        var registry = new TestPluginRegistry();
        registry.AddLogParser(new StubParser("plugin.broken", priority: 0)
        {
            Throw = new InvalidOperationException("boom"),
        });
        registry.AddLogParser(new StubParser("plugin.working", priority: 10)
        {
            Result = new LogParserResult("Working"),
        });
        using var enricher = new PluginLogEnricher(registry);
        var entry = CreateEntry();

        enricher.Enrich(entry, "serial");

        Assert.Equal("Working", entry.Tag);
    }

    [Fact]
    public void Enrich_PicksUpParsersRegisteredAfterConstruction()
    {
        var registry = new TestPluginRegistry();
        using var enricher = new PluginLogEnricher(registry);
        var entry = CreateEntry();

        enricher.Enrich(entry, "serial");
        Assert.Equal("AndroidRuntime", entry.Tag);

        registry.AddLogParser(new StubParser("plugin.late", priority: 0)
        {
            Result = new LogParserResult("LateTag"),
        });
        enricher.Enrich(entry, "serial");

        Assert.Equal("LateTag", entry.Tag);
    }

    private static LogEntry CreateEntry() => new()
    {
        Raw = "10-28 12:00:00.000  1234  1234 E AndroidRuntime: boom",
        Tag = "AndroidRuntime",
        Level = "E",
        Message = "boom",
    };

    private sealed class StubParser(string id, int priority) : ILogParser
    {
        public string Id => id;

        public string Name => id;

        public int Priority => priority;

        public LogParserResult? Result { get; init; }

        public Exception? Throw { get; init; }

        public List<LogParserContext> Contexts { get; } = [];

        public LogParserResult? Parse(LogParserContext context)
        {
            Contexts.Add(context);
            if (Throw is not null)
            {
                throw Throw;
            }

            return Result;
        }
    }
}
