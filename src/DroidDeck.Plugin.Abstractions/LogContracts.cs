namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// The parsed fields of one log line handed to a plugin parser.
/// </summary>
/// <param name="DeviceSerial">The device that produced the line, when known.</param>
/// <param name="RawLine">The unparsed log line.</param>
/// <param name="Tag">The parsed log tag.</param>
/// <param name="Level">The parsed log level.</param>
/// <param name="Message">The parsed message.</param>
public sealed record LogParserContext(
    string DeviceSerial,
    string RawLine,
    string Tag,
    string Level,
    string Message);

/// <summary>
/// Fields a plugin parser replaces on a log entry.
/// </summary>
/// <param name="Tag">The replacement tag, or <see langword="null"/> to keep it.</param>
/// <param name="Level">The replacement level, or <see langword="null"/> to keep it.</param>
/// <param name="Message">The replacement message, or <see langword="null"/> to keep it.</param>
/// <param name="HighlightRuleId">A highlight rule to apply to the entry, or <see langword="null"/>.</param>
public sealed record LogParserResult(
    string? Tag = null,
    string? Level = null,
    string? Message = null,
    string? HighlightRuleId = null);

/// <summary>
/// Enriches log entries for a stack. Parsers run on the log pipeline, never the UI thread.
/// </summary>
public interface ILogParser
{
    /// <summary>Gets the stable parser identifier.</summary>
    string Id { get; }

    /// <summary>Gets the display name.</summary>
    string Name { get; }

    /// <summary>Gets the parser priority; lower values run first.</summary>
    int Priority { get; }

    /// <summary>Enriches a line, returning <see langword="null"/> when the parser does not apply.</summary>
    LogParserResult? Parse(LogParserContext context);
}

/// <summary>
/// A highlight rule contributed by a plugin.
/// </summary>
/// <param name="Id">The stable rule identifier.</param>
/// <param name="Name">The display name.</param>
/// <param name="Pattern">The literal or regular expression pattern.</param>
/// <param name="IsRegex">Whether the pattern is a regular expression.</param>
/// <param name="Foreground">The foreground color as <c>#RRGGBB</c>.</param>
/// <param name="Background">The optional background color as <c>#RRGGBB</c>.</param>
/// <param name="Bold">Whether matching lines are bold.</param>
/// <param name="IsEnabled">Whether the rule starts enabled.</param>
public sealed record PluginHighlightRule(
    string Id,
    string Name,
    string Pattern,
    bool IsRegex = false,
    string Foreground = "#FF5555",
    string? Background = null,
    bool Bold = false,
    bool IsEnabled = true);

/// <summary>
/// Contributes highlight rules for the log view.
/// </summary>
public interface IHighlightRuleProvider
{
    /// <summary>Gets the stable provider identifier.</summary>
    string Id { get; }

    /// <summary>Gets the rules in evaluation order.</summary>
    IReadOnlyList<PluginHighlightRule> Rules { get; }
}
