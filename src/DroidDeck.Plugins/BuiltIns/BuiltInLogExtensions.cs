using DroidDeck.Core.Logs;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.BuiltIns;

/// <summary>
/// Flags Android crash and ANR lines so they keep the crash highlight even when
/// the active rules are reordered. Built-ins use the same parser contract as plugins.
/// </summary>
public sealed class AndroidCrashLogParser : ILogParser
{
    /// <summary>The stable parser identifier.</summary>
    public const string ParserId = "droiddeck.builtin.logParser.androidCrash";

    /// <inheritdoc />
    public string Id => ParserId;

    /// <inheritdoc />
    public string Name => "Android crash signals";

    /// <inheritdoc />
    public int Priority => -100;

    /// <inheritdoc />
    public LogParserResult? Parse(LogParserContext context)
    {
        if (context.Message.Contains("FATAL EXCEPTION", StringComparison.Ordinal))
        {
            return new LogParserResult(Level: "F", HighlightRuleId: "fatal");
        }

        if (context.Message.Contains("ANR in ", StringComparison.Ordinal))
        {
            return new LogParserResult(Level: "E", HighlightRuleId: "fatal");
        }

        return null;
    }
}

/// <summary>Contributes the built-in highlight rules through the plugin contract.</summary>
public sealed class BuiltInHighlightRuleProvider : IHighlightRuleProvider
{
    /// <summary>The stable provider identifier.</summary>
    public const string ProviderId = "droiddeck.builtin.highlightRules";

    private static readonly IReadOnlyList<PluginHighlightRule> BuiltInRules = HighlightRule.BuiltIn
        .Select(rule => new PluginHighlightRule(
            rule.Id,
            rule.Name,
            rule.Pattern,
            rule.IsRegex,
            rule.Foreground,
            rule.Background,
            rule.Bold,
            rule.IsEnabled))
        .ToArray();

    /// <inheritdoc />
    public string Id => ProviderId;

    /// <inheritdoc />
    public IReadOnlyList<PluginHighlightRule> Rules => BuiltInRules;
}
