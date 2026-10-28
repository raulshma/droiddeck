namespace DroidDeck.App.ViewModels;

/// <summary>Builds stable automation identifiers for generated rows.</summary>
public static class AutomationId
{
    /// <summary>Combines a prefix with an identifier UI Automation can match.</summary>
    public static string For(string prefix, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var safe = new string((value ?? string.Empty)
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray());
        return $"{prefix}_{safe}";
    }
}
