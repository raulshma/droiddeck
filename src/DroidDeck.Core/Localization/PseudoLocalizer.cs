using System.Text;

namespace DroidDeck.Core.Localization;

/// <summary>
/// Expands and accents English text so layouts can be checked for hardcoded
/// strings and truncation before real translations exist.
/// </summary>
public static class PseudoLocalizer
{
    /// <summary>Gets the marker every pseudo-localized string starts with.</summary>
    public const string Prefix = "[!!";

    /// <summary>Gets the marker every pseudo-localized string ends with.</summary>
    public const string Suffix = "!!]";

    /// <summary>Expands and accents text while preserving placeholders and whitespace.</summary>
    public static string Transform(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var targetLength = (int)Math.Ceiling(text.Length * 1.3);
        var builder = new StringBuilder(text.Length + 8);
        foreach (var character in text)
        {
            var accented = Accent(character);
            builder.Append(accented);
            if (IsVowel(character))
            {
                builder.Append(accented);
            }
        }

        while (builder.Length + Prefix.Length + Suffix.Length < targetLength)
        {
            builder.Append('~');
        }

        return string.Concat(Prefix, builder.ToString(), Suffix);
    }

    private static char Accent(char character) => character switch
    {
        'a' => 'á',
        'e' => 'é',
        'i' => 'í',
        'o' => 'ó',
        'u' => 'ú',
        'c' => 'ç',
        'n' => 'ñ',
        's' => 'š',
        'y' => 'ý',
        'z' => 'ž',
        'A' => 'Á',
        'E' => 'É',
        'I' => 'Í',
        'O' => 'Ó',
        'U' => 'Ú',
        'C' => 'Ç',
        'N' => 'Ñ',
        'S' => 'Š',
        'Y' => 'Ý',
        'Z' => 'Ž',
        _ => character,
    };

    private static bool IsVowel(char character) =>
        character is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'I' or 'O' or 'U';
}
