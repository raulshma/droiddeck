using System.Text.RegularExpressions;

namespace DroidDeck.Core.Processes;

/// <summary>Removes terminal control sequences from captured tool output.</summary>
public static partial class AnsiText
{
    /// <summary>Removes ANSI escape and OSC sequences from a single line of text.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EscapeSequence().Replace(text, string.Empty);
    }

    [GeneratedRegex(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])",
        RegexOptions.CultureInvariant)]
    private static partial Regex EscapeSequence();
}
