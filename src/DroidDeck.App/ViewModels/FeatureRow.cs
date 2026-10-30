using DroidDeck.Core.Features;
using Microsoft.UI.Xaml;

namespace DroidDeck.App.ViewModels;

/// <summary>One feature row in the settings Customize section.</summary>
public sealed class FeatureRow
{
    /// <summary>Creates a row for a feature with its capability badge.</summary>
    public FeatureRow(FeatureDefinition feature, CapabilityBadge badge, bool isHidden)
    {
        Feature = feature;
        Capability = badge;
        IsHidden = isHidden;
        ToggleId = AutomationId.For("FeatureToggle", feature.Id);
        BadgeId = AutomationId.For("FeatureBadge", feature.Id);
    }

    /// <summary>Gets the feature definition.</summary>
    public FeatureDefinition Feature { get; }

    /// <summary>Gets the badge explaining the support level.</summary>
    public CapabilityBadge Capability { get; }

    /// <summary>Gets whether the feature is currently hidden.</summary>
    public bool IsHidden { get; }

    /// <summary>Gets the feature identifier.</summary>
    public string Id => Feature.Id;

    /// <summary>Gets the feature name.</summary>
    public string Name => Feature.Name;

    /// <summary>Gets the description plus the badge explanation.</summary>
    public string Detail => $"{Feature.Description} · {Capability.Description}";

    /// <summary>Gets the category display name.</summary>
    public string Category => Feature.Category switch
    {
        FeatureCategory.Navigation => "Navigation",
        FeatureCategory.Toolbox => "Toolbox",
        FeatureCategory.HomeWidget => "Home",
        _ => "Stack",
    };

    /// <summary>Gets the badge title, which is the Hidden badge while hidden.</summary>
    public string Badge => Capability.Title;

    /// <summary>Gets the toggle button caption.</summary>
    public string ToggleLabel => IsHidden ? "Show" : "Hide";

    /// <summary>Gets the automation identifier of the toggle button.</summary>
    public string ToggleId { get; }

    /// <summary>Gets the automation identifier of the badge text.</summary>
    public string BadgeId { get; }
}
