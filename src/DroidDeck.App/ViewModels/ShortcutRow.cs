using Microsoft.UI.Xaml;

namespace DroidDeck.App.ViewModels;

/// <summary>One command row in the keyboard shortcuts settings section.</summary>
public sealed class ShortcutRow
{
    /// <summary>Creates a row for a command with its effective shortcut.</summary>
    public ShortcutRow(string id, string title, string category, string shortcut, bool hasOverride)
    {
        Id = id;
        Title = title;
        Category = category;
        Shortcut = shortcut;
        HasOverride = hasOverride;
    }

    /// <summary>Gets the command identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the command display title.</summary>
    public string Title { get; }

    /// <summary>Gets the command category.</summary>
    public string Category { get; }

    /// <summary>Gets the effective shortcut display, or "Not set".</summary>
    public string Shortcut { get; }

    /// <summary>Gets whether the user has overridden the default shortcut.</summary>
    public bool HasOverride { get; }

    /// <summary>Gets whether the Reset button is shown (only when an override exists).</summary>
    public Visibility ResetVisibility => HasOverride ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Gets the automation id for the Change button.</summary>
    public string ChangeId => $"ShortcutChange_{Suffix}";

    /// <summary>Gets the automation id for the Reset button.</summary>
    public string ResetId => $"ShortcutReset_{Suffix}";

    /// <summary>Gets the automation id for the shortcut text.</summary>
    public string ShortcutId => $"ShortcutValue_{Suffix}";

    private string Suffix => Id.Replace('.', '_').Replace('/', '_');
}
