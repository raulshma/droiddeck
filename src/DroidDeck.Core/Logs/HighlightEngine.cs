using System.Text.RegularExpressions;

namespace DroidDeck.Core.Logs;

/// <summary>
/// Compiles enabled highlight rules and finds the rule that colors a log line.
/// The first enabled rule whose pattern matches wins, so ordering is meaningful.
/// </summary>
public sealed class HighlightEngine
{
    private readonly List<(HighlightRule Rule, Regex Pattern)> _compiled = [];

    /// <summary>Compiles the enabled rules; invalid patterns are skipped.</summary>
    public HighlightEngine(IEnumerable<HighlightRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        foreach (var rule in rules)
        {
            if (!rule.IsEnabled || string.IsNullOrWhiteSpace(rule.Pattern))
            {
                continue;
            }

            var pattern = rule.Pattern.Trim();
            if (!rule.IsRegex)
            {
                pattern = Regex.Escape(pattern);
            }

            try
            {
                var regex = new Regex(
                    pattern,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1));
                _compiled.Add((rule, regex));
            }
            catch (ArgumentException)
            {
                // An invalid user pattern must not break the log view.
            }
        }
    }

    /// <summary>Gets the rules that were compiled, in evaluation order.</summary>
    public IReadOnlyList<HighlightRule> Rules => _compiled.Select(entry => entry.Rule).ToArray();

    /// <summary>
    /// Returns the rule for the line: a parser-assigned rule id wins, then the
    /// first enabled rule whose pattern matches.
    /// </summary>
    public HighlightRule? Match(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrEmpty(entry.HighlightRuleId))
        {
            foreach (var (rule, _) in _compiled)
            {
                if (string.Equals(rule.Id, entry.HighlightRuleId, StringComparison.OrdinalIgnoreCase))
                {
                    return rule;
                }
            }
        }

        foreach (var (rule, regex) in _compiled)
        {
            if (regex.IsMatch(entry.Raw))
            {
                return rule;
            }
        }

        return null;
    }
}
