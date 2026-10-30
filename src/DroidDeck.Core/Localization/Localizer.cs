using System.Globalization;

namespace DroidDeck.Core.Localization;

/// <summary>
/// Resolves catalog strings for the configured language. English is the source
/// catalog; the pseudo-locale renders it expanded for localization checks.
/// </summary>
public sealed class Localizer : ILocalizer
{
    /// <summary>The culture name reported for the pseudo-locale test language.</summary>
    public const string PseudoLocaleName = "qps-ploc";

    /// <summary>The prefix applied to resource keys exposed to XAML.</summary>
    public const string ResourcePrefix = "Loc.";

    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    private CultureInfo _culture = EnglishCulture;
    private IReadOnlyDictionary<string, string> _strings = LocalizationCatalog.English;

    /// <summary>Creates a localizer for a language preference, optionally overriding the system culture.</summary>
    public Localizer(string? preference = null, CultureInfo? systemCulture = null) =>
        Apply(preference, systemCulture, raiseChanged: false);

    /// <summary>Raised after <see cref="Update"/> changes the language.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public string Language { get; private set; } = EnglishCulture.Name;

    /// <inheritdoc />
    public bool IsPseudoLocale { get; private set; }

    /// <inheritdoc />
    public bool IsRightToLeft { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Resources { get; private set; } =
        BuildResources(LocalizationCatalog.English);

    /// <summary>Gets the normalized preference the localizer was created with.</summary>
    public string Preference { get; private set; } = LocalizationPreferences.System;

    /// <summary>Changes the language preference and raises <see cref="Changed"/>.</summary>
    public void Update(string? preference, CultureInfo? systemCulture = null) =>
        Apply(preference, systemCulture, raiseChanged: true);

    /// <inheritdoc />
    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _strings.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"The localization key '{key}' is not defined.");
    }

    /// <inheritdoc />
    public string GetOrFallback(string key, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _strings.TryGetValue(key, out var value) ? value : fallback;
    }

    /// <inheritdoc />
    public string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    /// <summary>Gets the resource key XAML uses for a catalog key.</summary>
    public static string ResourceKey(string key) => ResourcePrefix + key;

    private void Apply(string? preference, CultureInfo? systemCulture, bool raiseChanged)
    {
        var (resolvedPreference, culture, pseudo) = Resolve(preference, systemCulture);
        var effective = new Dictionary<string, string>(LocalizationCatalog.English.Count, StringComparer.Ordinal);
        var resources = new Dictionary<string, string>(LocalizationCatalog.English.Count, StringComparer.Ordinal);
        foreach (var (key, english) in LocalizationCatalog.English)
        {
            var value = pseudo ? PseudoLocalizer.Transform(english) : english;
            effective[key] = value;
            resources[ResourceKey(key)] = value;
        }

        Preference = resolvedPreference;
        _culture = culture;
        Language = pseudo ? PseudoLocaleName : culture.Name;
        IsPseudoLocale = pseudo;
        IsRightToLeft = !pseudo && culture.TextInfo.IsRightToLeft;
        _strings = effective;
        Resources = resources;

        if (raiseChanged)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static (string Preference, CultureInfo Culture, bool Pseudo) Resolve(
        string? preference,
        CultureInfo? systemCulture)
    {
        var value = preference?.Trim();
        if (string.IsNullOrEmpty(value) ||
            string.Equals(value, LocalizationPreferences.System, StringComparison.OrdinalIgnoreCase))
        {
            return (LocalizationPreferences.System, ResolveSystemCulture(systemCulture), false);
        }

        if (string.Equals(value, LocalizationPreferences.Pseudo, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, PseudoLocaleName, StringComparison.OrdinalIgnoreCase))
        {
            return (LocalizationPreferences.Pseudo, EnglishCulture, true);
        }

        if (string.Equals(value, LocalizationPreferences.English, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "en", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "en-US", StringComparison.OrdinalIgnoreCase))
        {
            return (LocalizationPreferences.English, EnglishCulture, false);
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(value);
            return (value, string.IsNullOrEmpty(culture.Name) ? EnglishCulture : culture, false);
        }
        catch (CultureNotFoundException)
        {
            return (LocalizationPreferences.System, ResolveSystemCulture(systemCulture), false);
        }
    }

    private static CultureInfo ResolveSystemCulture(CultureInfo? systemCulture)
    {
        var culture = systemCulture ?? CultureInfo.CurrentUICulture;
        return string.IsNullOrEmpty(culture.Name) ? EnglishCulture : culture;
    }

    private static Dictionary<string, string> BuildResources(
        IReadOnlyDictionary<string, string> strings)
    {
        var resources = new Dictionary<string, string>(strings.Count, StringComparer.Ordinal);
        foreach (var (key, value) in strings)
        {
            resources[ResourceKey(key)] = value;
        }

        return resources;
    }
}
