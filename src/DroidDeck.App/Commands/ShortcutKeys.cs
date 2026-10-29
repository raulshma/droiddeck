using Windows.System;

namespace DroidDeck.App.Commands;

/// <summary>
/// Maps between canonical gesture key names (see DroidDeck.Core.Commands.KeyGesture)
/// and <see cref="VirtualKey"/> values used by accelerators and key capture.
/// </summary>
internal static class ShortcutKeys
{
    private static readonly Dictionary<string, VirtualKey> NamedKeys = new(StringComparer.Ordinal)
    {
        ["Escape"] = VirtualKey.Escape,
        ["Enter"] = VirtualKey.Enter,
        ["Space"] = VirtualKey.Space,
        ["Tab"] = VirtualKey.Tab,
        ["Back"] = VirtualKey.Back,
        ["Delete"] = VirtualKey.Delete,
        ["Insert"] = VirtualKey.Insert,
        ["Home"] = VirtualKey.Home,
        ["End"] = VirtualKey.End,
        ["PageUp"] = VirtualKey.PageUp,
        ["PageDown"] = VirtualKey.PageDown,
        ["Up"] = VirtualKey.Up,
        ["Down"] = VirtualKey.Down,
        ["Left"] = VirtualKey.Left,
        ["Right"] = VirtualKey.Right,
    };

    /// <summary>Resolves a canonical gesture key name to a virtual key.</summary>
    public static bool TryGetVirtualKey(string token, out VirtualKey key)
    {
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
        {
            key = (VirtualKey)char.ToUpperInvariant(token[0]);
            return true;
        }

        if (token.Length is 2 or 3 && char.ToUpperInvariant(token[0]) == 'F' &&
            int.TryParse(token[1..], out var function) && function is >= 1 and <= 24)
        {
            // VirtualKey.F1 is 0x70 (112); F1 through F24 are contiguous.
            key = (VirtualKey)(111 + function);
            return true;
        }

        return NamedKeys.TryGetValue(token, out key);
    }

    /// <summary>Resolves a virtual key to a canonical gesture key name.</summary>
    public static bool TryGetToken(VirtualKey key, out string token)
    {
        var value = (int)key;
        if (value is (>= 65 and <= 90) or (>= 48 and <= 57))
        {
            token = char.ToUpperInvariant((char)value).ToString();
            return true;
        }

        if (value is >= 112 and <= 135)
        {
            token = $"F{value - 111}";
            return true;
        }

        foreach (var pair in NamedKeys)
        {
            if (pair.Value == key)
            {
                token = pair.Key;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    /// <summary>Gets whether the key is a bare modifier held during a combination.</summary>
    public static bool IsModifier(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;
}
