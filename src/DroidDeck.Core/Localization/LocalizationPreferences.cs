namespace DroidDeck.Core.Localization;

/// <summary>The supported values of the interface language setting.</summary>
public static class LocalizationPreferences
{
    /// <summary>Follows the operating system's UI language.</summary>
    public const string System = "system";

    /// <summary>Uses the English catalog regardless of the operating system.</summary>
    public const string English = "english";

    /// <summary>Renders the English catalog through the pseudo-locale test transform.</summary>
    public const string Pseudo = "pseudo";
}
