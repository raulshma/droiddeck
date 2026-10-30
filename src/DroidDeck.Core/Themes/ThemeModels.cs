using System.Text.Json.Serialization;

namespace DroidDeck.Core.Themes;

/// <summary>The base palette a theme builds on.</summary>
public enum ThemeBase
{
    /// <summary>A light palette.</summary>
    Light,

    /// <summary>A dark palette.</summary>
    Dark,

    /// <summary>A system high-contrast palette.</summary>
    HighContrast,
}

/// <summary>Named colors used by the shell and plugin host.</summary>
public sealed class ThemeColorTokens
{
    /// <summary>Gets or sets the window background.</summary>
    public string Background { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets the standard card and panel surface.</summary>
    public string Surface { get; set; } = "#F7F7F8";

    /// <summary>Gets or sets the alternate surface used for stripes and insets.</summary>
    public string SurfaceAlt { get; set; } = "#EFEFF1";

    /// <summary>Gets or sets primary text.</summary>
    public string Text { get; set; } = "#1A1A1A";

    /// <summary>Gets or sets secondary text.</summary>
    public string TextMuted { get; set; } = "#5F6368";

    /// <summary>Gets or sets the accent used for primary actions.</summary>
    public string Accent { get; set; } = "#0067C0";

    /// <summary>Gets or sets text and icons drawn on the accent.</summary>
    public string AccentText { get; set; } = "#FFFFFF";

    /// <summary>Gets or sets borders and separators.</summary>
    public string Border { get; set; } = "#8E8E93";

    /// <summary>Gets or sets the success color.</summary>
    public string Success { get; set; } = "#0F7B0F";

    /// <summary>Gets or sets the warning color.</summary>
    public string Warning { get; set; } = "#9D5D00";

    /// <summary>Gets or sets the error color.</summary>
    public string Error { get; set; } = "#C42B1C";

    /// <summary>Gets or sets the keyboard focus ring color.</summary>
    public string Focus { get; set; } = "#0067C0";

    /// <summary>Returns a deep copy of the tokens.</summary>
    public ThemeColorTokens Clone() => (ThemeColorTokens)MemberwiseClone();
}

/// <summary>A theme with color tokens, fonts, and log colors.</summary>
public sealed class ThemeDefinition
{
    /// <summary>Gets or sets the theme schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Gets or sets the stable theme identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the base palette.</summary>
    public ThemeBase Base { get; set; } = ThemeBase.Light;

    /// <summary>Gets or sets the color tokens.</summary>
    public ThemeColorTokens Colors { get; set; } = new();

    /// <summary>Gets or sets the UI font family; empty uses the host default.</summary>
    public string FontFamily { get; set; } = string.Empty;

    /// <summary>Gets or sets the monospace font family used by logs and code.</summary>
    public string MonoFontFamily { get; set; } = "Cascadia Mono";

    /// <summary>Gets or sets the base font size in effective pixels.</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>Gets or sets whether motion is reduced for this theme.</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>Gets or sets log level to color token names, such as <c>error</c> to <c>error</c>.</summary>
    public Dictionary<string, string> LogColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; set; }

    /// <summary>Returns a deep copy of the theme.</summary>
    public ThemeDefinition Clone() =>
        DroidDeck.Core.Infrastructure.DroidDeckJson.Deserialize<ThemeDefinition>(
            DroidDeck.Core.Infrastructure.DroidDeckJson.Serialize(this))
        ?? throw new InvalidOperationException("The theme could not be copied.");
}

/// <summary>One contrast check with the measured and required ratios.</summary>
/// <param name="Pair">The token pair, such as <c>text on background</c>.</param>
/// <param name="Ratio">The measured WCAG contrast ratio.</param>
/// <param name="Required">The minimum ratio for this kind of text or component.</param>
/// <param name="IsError">Whether the failure blocks the theme.</param>
/// <param name="Explanation">A user-facing explanation of the result.</param>
public sealed record ThemeContrastFinding(
    string Pair,
    double Ratio,
    double Required,
    bool IsError,
    string Explanation);

/// <summary>The result of validating a theme's contrast.</summary>
/// <param name="Findings">Every checked pair, including passing pairs.</param>
public sealed record ThemeValidationResult(IReadOnlyList<ThemeContrastFinding> Findings)
{
    /// <summary>Gets whether every required pair passes.</summary>
    public bool IsValid => !Findings.Any(static finding => finding.IsError);
}

/// <summary>Raised when a theme has unreadable color combinations.</summary>
public sealed class ThemeValidationException(ThemeValidationResult result)
    : Exception(BuildMessage(result))
{
    /// <summary>Gets the validation result that blocked the theme.</summary>
    public ThemeValidationResult Result { get; } = result;

    private static string BuildMessage(ThemeValidationResult result)
    {
        var failures = result.Findings
            .Where(static finding => finding.IsError)
            .Select(static finding => finding.Explanation);
        return "The theme cannot be applied: " + string.Join(" ", failures);
    }
}
