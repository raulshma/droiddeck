using DroidDeck.Core.Profiles;
using Microsoft.UI.Xaml;

namespace DroidDeck.App.ViewModels;

/// <summary>One profile row in the settings profiles section.</summary>
public sealed class ProfileRow
{
    /// <summary>Creates a row for a profile, marking the active one.</summary>
    public ProfileRow(ProfileMetadata metadata, bool isActive)
    {
        Metadata = metadata;
        IsActive = isActive;
        NameId = AutomationId.For("ProfileName", metadata.Id);
        ApplyId = AutomationId.For("ProfileApply", metadata.Id);
        ExportId = AutomationId.For("ProfileExport", metadata.Id);
        RenameId = AutomationId.For("ProfileRename", metadata.Id);
        DuplicateId = AutomationId.For("ProfileDuplicate", metadata.Id);
        DeleteId = AutomationId.For("ProfileDelete", metadata.Id);
        MoreId = AutomationId.For("ProfileMore", metadata.Id);
    }

    /// <summary>Gets the profile metadata.</summary>
    public ProfileMetadata Metadata { get; }

    /// <summary>Gets whether this profile is applied right now.</summary>
    public bool IsActive { get; }

    /// <summary>Gets the profile identifier.</summary>
    public string Id => Metadata.Id;

    /// <summary>Gets the profile name.</summary>
    public string Name => Metadata.Name;

    /// <summary>Gets the description and last-updated line.</summary>
    public string Detail => Metadata.Description.Length == 0
        ? $"Updated {Metadata.UpdatedAt.ToLocalTime():g}"
        : $"{Metadata.Description} · Updated {Metadata.UpdatedAt.ToLocalTime():g}";

    /// <summary>Gets the badge shown on the active profile.</summary>
    public string ActiveBadge => IsActive ? "Active" : string.Empty;

    /// <summary>Gets whether the active badge is visible.</summary>
    public Visibility ActiveVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Gets whether the Apply button is visible (hidden while active).</summary>
    public Visibility ApplyVisibility => IsActive ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Gets the automation identifier of the name text.</summary>
    public string NameId { get; }

    /// <summary>Gets the automation identifier of the apply button.</summary>
    public string ApplyId { get; }

    /// <summary>Gets the automation identifier of the export button.</summary>
    public string ExportId { get; }

    /// <summary>Gets the automation identifier of the rename action.</summary>
    public string RenameId { get; }

    /// <summary>Gets the automation identifier of the duplicate action.</summary>
    public string DuplicateId { get; }

    /// <summary>Gets the automation identifier of the delete action.</summary>
    public string DeleteId { get; }

    /// <summary>Gets the automation identifier of the more menu.</summary>
    public string MoreId { get; }
}
