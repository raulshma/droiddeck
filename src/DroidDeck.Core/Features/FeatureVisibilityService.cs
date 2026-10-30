using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Features;

/// <summary>
/// Applies Customize-mode visibility on top of the feature catalog. Hiding a
/// feature only records its identifier in <c>features.hidden</c>; settings and
/// data owned by the feature are never deleted, so showing it again restores the
/// previous state.
/// </summary>
public sealed class FeatureVisibilityService
{
    private readonly IConfigurationService _configuration;

    /// <summary>Creates the service over the configuration snapshot.</summary>
    public FeatureVisibilityService(IConfigurationService configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    /// <summary>Gets every feature, including hidden ones.</summary>
    public IReadOnlyList<FeatureDefinition> Features { get; } = FeatureCatalog.Default;

    /// <summary>Gets the identifiers currently hidden by the user.</summary>
    public IReadOnlyList<string> HiddenFeatures =>
        _configuration.GetValue<List<string>>(SettingsPaths.HiddenFeatures) ?? [];

    /// <summary>Gets whether a feature is visible in navigation and home.</summary>
    public bool IsVisible(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        return !HiddenFeatures.Contains(featureId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets the visible features in one category, preserving catalog order.</summary>
    public IReadOnlyList<FeatureDefinition> GetVisible(FeatureCategory category) =>
        FeatureCatalog.Default
            .Where(feature => feature.Category == category && IsVisible(feature.Id))
            .ToArray();

    /// <summary>Gets the hidden features, including identifiers from newer versions.</summary>
    public IReadOnlyList<FeatureDefinition> GetHidden() =>
        HiddenFeatures
            .Select(FeatureCatalog.Find)
            .Where(static feature => feature is not null)
            .Select(static feature => feature!)
            .ToArray();

    /// <summary>Hides a feature without touching its configuration.</summary>
    public Task HideAsync(string featureId, CancellationToken cancellationToken = default)
    {
        RequireKnown(featureId);
        var hidden = HiddenFeatures.ToList();
        if (!hidden.Contains(featureId, StringComparer.OrdinalIgnoreCase))
        {
            hidden.Add(featureId);
        }

        return _configuration.SetAsync(SettingsPaths.HiddenFeatures, hidden, cancellationToken: cancellationToken);
    }

    /// <summary>Shows a previously hidden feature, restoring its navigation entry.</summary>
    public Task ShowAsync(string featureId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        var hidden = HiddenFeatures
            .Where(id => !string.Equals(id, featureId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return _configuration.SetAsync(SettingsPaths.HiddenFeatures, hidden, cancellationToken: cancellationToken);
    }

    /// <summary>Shows every hidden feature.</summary>
    public Task ShowAllAsync(CancellationToken cancellationToken = default) =>
        _configuration.SetAsync(SettingsPaths.HiddenFeatures, new List<string>(), cancellationToken: cancellationToken);

    /// <summary>Gets the badge shown next to a feature, including the hidden state.</summary>
    public CapabilityBadge GetBadge(string featureId, FeatureCapability? capability = null)
    {
        RequireKnown(featureId);
        if (!IsVisible(featureId))
        {
            return CapabilityBadges.For(FeatureCapability.Hidden);
        }

        var feature = FeatureCatalog.Find(featureId)!;
        return CapabilityBadges.For(capability ?? feature.DefaultCapability);
    }

    private static void RequireKnown(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        if (FeatureCatalog.Find(featureId) is null)
        {
            throw new ArgumentException($"'{featureId}' is not a known feature.", nameof(featureId));
        }
    }
}
