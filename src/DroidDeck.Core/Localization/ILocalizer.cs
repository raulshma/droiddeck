namespace DroidDeck.Core.Localization;

/// <summary>Resolves interface strings for the active language.</summary>
public interface ILocalizer
{
    /// <summary>Gets the culture name the interface is using, such as <c>en-US</c> or <c>qps-ploc</c>.</summary>
    string Language { get; }

    /// <summary>Gets whether strings are expanded and accented for a localization test run.</summary>
    bool IsPseudoLocale { get; }

    /// <summary>Gets whether the active culture reads right to left.</summary>
    bool IsRightToLeft { get; }

    /// <summary>Gets the translated strings keyed for XAML static-resource lookup.</summary>
    IReadOnlyDictionary<string, string> Resources { get; }

    /// <summary>Gets a translated string, throwing when the key is not defined.</summary>
    string Get(string key);

    /// <summary>Gets a translated string, or the supplied fallback for dynamic content.</summary>
    string GetOrFallback(string key, string fallback);

    /// <summary>Gets a translated string with its placeholders filled in.</summary>
    string Format(string key, params object?[] args);
}
