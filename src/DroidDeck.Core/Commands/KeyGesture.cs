using System.Text;

namespace DroidDeck.Core.Commands;

/// <summary>Modifier keys held while the shortcut key is pressed.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifiers.</summary>
    None = 0,

    /// <summary>The Ctrl key.</summary>
    Control = 1,

    /// <summary>The Shift key.</summary>
    Shift = 2,

    /// <summary>The Alt key.</summary>
    Alt = 4,

    /// <summary>The Windows key.</summary>
    Windows = 8,
}

/// <summary>
/// A single key combined with optional modifiers. Chords are not supported; a
/// gesture is one non-modifier key plus zero or more modifiers.
/// </summary>
/// <param name="Modifiers">The modifiers held during the shortcut.</param>
/// <param name="Key">The canonical key name, for example <c>K</c> or <c>F5</c>.</param>
public sealed record KeyGesture(KeyModifiers Modifiers, string Key)
{
    private const char Separator = '+';

    private static readonly string[] ModifierOrder = ["Ctrl", "Shift", "Alt", "Win"];

    private static readonly KeyModifiers[] ModifierFlags =
    [
        KeyModifiers.Control,
        KeyModifiers.Shift,
        KeyModifiers.Alt,
        KeyModifiers.Windows,
    ];

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "Escape",
        ["escape"] = "Escape",
        ["return"] = "Enter",
        ["enter"] = "Enter",
        ["space"] = "Space",
        ["spacebar"] = "Space",
        ["tab"] = "Tab",
        ["back"] = "Back",
        ["backspace"] = "Back",
        ["del"] = "Delete",
        ["delete"] = "Delete",
        ["ins"] = "Insert",
        ["insert"] = "Insert",
        ["home"] = "Home",
        ["end"] = "End",
        ["pageup"] = "PageUp",
        ["pagedown"] = "PageDown",
        ["up"] = "Up",
        ["down"] = "Down",
        ["left"] = "Left",
        ["right"] = "Right",
    };

    /// <summary>Parses a gesture like <c>Ctrl+Shift+S</c>, throwing when malformed.</summary>
    public static KeyGesture Parse(string text)
    {
        if (!TryParse(text, out var gesture))
        {
            throw new ArgumentException(
                $"'{text}' is not a shortcut. Use one key with optional Ctrl, Shift, Alt, and Win modifiers.",
                nameof(text));
        }

        return gesture;
    }

    /// <summary>Attempts to parse a gesture like <c>Ctrl+Shift+S</c>.</summary>
    public static bool TryParse(string? text, out KeyGesture gesture)
    {
        gesture = new KeyGesture(KeyModifiers.None, string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var tokens = text.Split(
            Separator,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 && LooksLikeModifier(tokens is [var only] ? only : string.Empty))
        {
            return false;
        }

        KeyModifiers modifiers = KeyModifiers.None;
        string? key = null;
        foreach (var token in tokens)
        {
            if (LooksLikeModifier(token))
            {
                if (key is not null || (modifiers & ModifierFor(token)) != 0)
                {
                    return false;
                }

                modifiers |= ModifierFor(token);
            }
            else if (key is null)
            {
                key = NormalizeKey(token);
            }
            else
            {
                return false;
            }
        }

        if (key is null)
        {
            return false;
        }

        gesture = new KeyGesture(modifiers, key);
        return true;
    }

    /// <summary>Returns the canonical display text, for example <c>Ctrl+Shift+S</c>.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < ModifierFlags.Length; index++)
        {
            if ((Modifiers & ModifierFlags[index]) != 0)
            {
                builder.Append(ModifierOrder[index]).Append(Separator);
            }
        }

        builder.Append(Key);
        return builder.ToString();
    }

    private static bool LooksLikeModifier(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" => true,
        "shift" => true,
        "alt" => true,
        "win" or "windows" or "meta" => true,
        _ => false,
    };

    private static KeyModifiers ModifierFor(string token) => token.ToLowerInvariant() switch
    {
        "ctrl" or "control" => KeyModifiers.Control,
        "shift" => KeyModifiers.Shift,
        "alt" => KeyModifiers.Alt,
        _ => KeyModifiers.Windows,
    };

    private static string NormalizeKey(string token)
    {
        if (token.Length == 1)
        {
            return char.ToUpperInvariant(token[0]).ToString();
        }

        if (token.Length is 2 && (char.ToLowerInvariant(token[0]) == 'f') && char.IsAsciiDigit(token[1]))
        {
            return "F" + token[1];
        }

        if (token.Length is 3 && char.ToLowerInvariant(token[0]) == 'f' &&
            char.IsAsciiDigit(token[1]) && char.IsAsciiDigit(token[2]))
        {
            return "F" + token[1..];
        }

        return KeyAliases.TryGetValue(token, out var canonical) ? canonical : token;
    }
}
