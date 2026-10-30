using System.Globalization;

namespace DroidDeck.Core.Themes;

/// <summary>Computes WCAG contrast ratios and validates theme token pairs.</summary>
public static class ThemeContrast
{
    /// <summary>The WCAG AA minimum for normal text.</summary>
    public const double NormalTextMinimum = 4.5;

    /// <summary>The WCAG AA minimum for large text and UI components.</summary>
    public const double LargeTextMinimum = 3.0;

    /// <summary>Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c> and returns the sRGB components.</summary>
    public static bool TryParseColor(string? text, out byte red, out byte green, out byte blue)
    {
        red = green = blue = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length == 8)
        {
            value = value[2..];
        }

        if (value.Length != 6 ||
            !byte.TryParse(value[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red) ||
            !byte.TryParse(value[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green) ||
            !byte.TryParse(value[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue))
        {
            red = green = blue = 0;
            return false;
        }

        return true;
    }

    /// <summary>Calculates the contrast ratio between two colors, from 1 to 21.</summary>
    public static double Ratio(string foreground, string background)
    {
        if (!TryParseColor(foreground, out var fr, out var fg, out var fb))
        {
            throw new ArgumentException($"'{foreground}' is not a color.", nameof(foreground));
        }

        if (!TryParseColor(background, out var br, out var bg, out var bb))
        {
            throw new ArgumentException($"'{background}' is not a color.", nameof(background));
        }

        var foregroundLuminance = Luminance(fr, fg, fb);
        var backgroundLuminance = Luminance(br, bg, bb);
        var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
        var darker = Math.Min(foregroundLuminance, backgroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>Validates the color pairs that carry text and state in the shell.</summary>
    public static ThemeValidationResult Validate(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var colors = theme.Colors ?? throw new ArgumentException("The theme has no color tokens.", nameof(theme));
        var findings = new List<ThemeContrastFinding>
        {
            Check("text on background", colors.Text, colors.Background, NormalTextMinimum),
            Check("text on surface", colors.Text, colors.Surface, NormalTextMinimum),
            Check("muted text on surface", colors.TextMuted, colors.Surface, NormalTextMinimum),
            Check("accent text on accent", colors.AccentText, colors.Accent, NormalTextMinimum),
            Check("success on surface", colors.Success, colors.Surface, LargeTextMinimum),
            Check("warning on surface", colors.Warning, colors.Surface, LargeTextMinimum),
            Check("error on surface", colors.Error, colors.Surface, LargeTextMinimum),
            Check("border on background", colors.Border, colors.Background, LargeTextMinimum),
            Check("focus on background", colors.Focus, colors.Background, LargeTextMinimum),
        };

        return new ThemeValidationResult(findings);
    }

    private static ThemeContrastFinding Check(string pair, string foreground, string background, double required)
    {
        double ratio;
        try
        {
            ratio = Ratio(foreground, background);
        }
        catch (ArgumentException)
        {
            return new ThemeContrastFinding(
                pair,
                0,
                required,
                IsError: true,
                $"{pair} uses an invalid color; use #RRGGBB values.");
        }

        var rounded = Math.Round(ratio, 2);
        return new ThemeContrastFinding(
            pair,
            rounded,
            required,
            IsError: rounded < required,
            rounded < required
                ? $"{pair} has a contrast ratio of {rounded:0.00}:1, below the required {required:0.0}:1."
                : $"{pair} has a contrast ratio of {rounded:0.00}:1, meeting the required {required:0.0}:1.");
    }

    private static double Luminance(byte red, byte green, byte blue)
    {
        static double Channel(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045
                ? channel / 12.92
                : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(red)) + (0.7152 * Channel(green)) + (0.0722 * Channel(blue));
    }
}
