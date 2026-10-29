using System.Globalization;

namespace DroidDeck.Core.Toolbox;

/// <summary>Pixel bounds reported by a uiautomator hierarchy node.</summary>
public readonly record struct UiBounds(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the width in pixels.</summary>
    public int Width => Math.Max(0, Right - Left);

    /// <summary>Gets the height in pixels.</summary>
    public int Height => Math.Max(0, Bottom - Top);

    /// <summary>Gets the horizontal center used for a tap.</summary>
    public int CenterX => Left + (Width / 2);

    /// <summary>Gets the vertical center used for a tap.</summary>
    public int CenterY => Top + (Height / 2);

    /// <summary>Gets whether the bounds enclose at least one pixel.</summary>
    public bool HasArea => Width > 0 && Height > 0;

    /// <summary>Parses the <c>[left,top][right,bottom]</c> bounds attribute.</summary>
    public static bool TryParse(string? text, out UiBounds bounds)
    {
        bounds = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(
            ['[', ']', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var left) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var right) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bottom))
        {
            return false;
        }

        bounds = new UiBounds(left, top, right, bottom);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"[{Left},{Top}][{Right},{Bottom}]";
}
