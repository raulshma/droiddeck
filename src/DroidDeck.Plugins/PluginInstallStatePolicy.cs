using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins.Catalog;

namespace DroidDeck.Plugins;

/// <summary>The actions the extensions page can offer for one plugin.</summary>
[Flags]
public enum PluginInstallActions
{
    /// <summary>No action is available.</summary>
    None = 0,

    /// <summary>The plugin is in the catalog but not installed.</summary>
    Install = 1,

    /// <summary>A newer compatible catalog version is available.</summary>
    Update = 2,

    /// <summary>A rollback backup is available.</summary>
    Rollback = 4,

    /// <summary>The plugin can be enabled.</summary>
    Enable = 8,

    /// <summary>The plugin can be disabled.</summary>
    Disable = 16,
}

/// <summary>
/// The actions and labels the extensions page shows for one plugin, derived
/// from its installed status, the catalog entries for this host, and the
/// rollback backup version.
/// </summary>
public sealed record PluginInstallState
{
    /// <summary>Gets the available actions.</summary>
    public PluginInstallActions Actions { get; init; }

    /// <summary>Gets the catalog version an install would use, when the plugin is not installed.</summary>
    public string? InstallVersion { get; init; }

    /// <summary>Gets the catalog version an update would use, when one is available.</summary>
    public string? UpdateVersion { get; init; }

    /// <summary>Gets the version a rollback would restore, when a backup exists.</summary>
    public string? RollbackVersion { get; init; }

    /// <summary>Gets the installed plugin's reason text, when one exists.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets whether a catalog install is available.</summary>
    public bool CanInstall => Has(PluginInstallActions.Install);

    /// <summary>Gets whether a catalog update is available.</summary>
    public bool CanUpdate => Has(PluginInstallActions.Update);

    /// <summary>Gets whether a rollback backup is available.</summary>
    public bool CanRollback => Has(PluginInstallActions.Rollback);

    /// <summary>Gets whether the plugin can be enabled.</summary>
    public bool CanEnable => Has(PluginInstallActions.Enable);

    /// <summary>Gets whether the plugin can be disabled.</summary>
    public bool CanDisable => Has(PluginInstallActions.Disable);

    /// <summary>Gets the update button label, when an update is available.</summary>
    public string UpdateLabel => UpdateVersion is null ? string.Empty : $"Update to v{UpdateVersion}";

    /// <summary>Gets the rollback button label, when a backup exists.</summary>
    public string RollbackLabel => RollbackVersion is null ? string.Empty : $"Roll back to v{RollbackVersion}";

    private bool Has(PluginInstallActions action) => (Actions & action) != 0;
}

/// <summary>
/// The one install/update/rollback policy for the extensions page. It selects
/// the highest compatible catalog version, keeps update detection and rollback
/// availability in one place, and leaves the page to only perform the chosen
/// action.
/// </summary>
public static class PluginInstallStatePolicy
{
    /// <summary>Computes the state of an installed plugin.</summary>
    /// <param name="status">The installed plugin's manager status.</param>
    /// <param name="catalogEntries">The catalog entries browsed for this host.</param>
    /// <param name="rollbackVersion">The version kept for rollback, when one exists.</param>
    public static PluginInstallState Evaluate(
        PluginStatus status,
        IReadOnlyList<CatalogEntry> catalogEntries,
        string? rollbackVersion)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(catalogEntries);
        var (actions, installVersion, updateVersion) = EvaluateActions(
            status.Id,
            status.Version,
            status.State,
            catalogEntries,
            rollbackVersion);
        return new PluginInstallState
        {
            Actions = actions,
            InstallVersion = installVersion,
            UpdateVersion = updateVersion,
            RollbackVersion = rollbackVersion,
            Reason = status.Reason,
        };
    }

    /// <summary>Computes the state of a catalog plugin that is not installed.</summary>
    /// <param name="pluginId">The catalog plugin identifier.</param>
    /// <param name="catalogEntries">The catalog entries browsed for this host.</param>
    public static PluginInstallState EvaluateNotInstalled(
        string pluginId,
        IReadOnlyList<CatalogEntry> catalogEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(catalogEntries);
        var (actions, installVersion, updateVersion) = EvaluateActions(
            pluginId,
            null,
            null,
            catalogEntries,
            null);
        return new PluginInstallState
        {
            Actions = actions,
            InstallVersion = installVersion,
            UpdateVersion = updateVersion,
        };
    }

    /// <summary>Computes the state of every installed plugin.</summary>
    /// <param name="statuses">The installed plugins.</param>
    /// <param name="catalogEntries">The catalog entries browsed for this host.</param>
    /// <param name="rollbackVersions">The rollback backup version keyed by plugin id.</param>
    public static IReadOnlyDictionary<string, PluginInstallState> EvaluateAll(
        IReadOnlyList<PluginStatus> statuses,
        IReadOnlyList<CatalogEntry> catalogEntries,
        IReadOnlyDictionary<string, string> rollbackVersions)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(catalogEntries);
        ArgumentNullException.ThrowIfNull(rollbackVersions);
        var states = new Dictionary<string, PluginInstallState>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses)
        {
            ArgumentNullException.ThrowIfNull(status);
            var rollbackVersion = rollbackVersions.TryGetValue(status.Id, out var version) ? version : null;
            states[status.Id] = Evaluate(status, catalogEntries, rollbackVersion);
        }

        return states;
    }

    private static (PluginInstallActions Actions, string? InstallVersion, string? UpdateVersion) EvaluateActions(
        string pluginId,
        string? installedVersion,
        PluginState? state,
        IReadOnlyList<CatalogEntry> catalogEntries,
        string? rollbackVersion)
    {
        var actions = PluginInstallActions.None;
        string? installVersion = null;
        string? updateVersion = null;
        if (installedVersion is null)
        {
            installVersion = SelectBestVersion(pluginId, catalogEntries, minimumExclusive: null);
            if (installVersion is not null)
            {
                actions |= PluginInstallActions.Install;
            }
        }
        else if (SemanticVersion.TryParse(installedVersion, out var current))
        {
            updateVersion = SelectBestVersion(pluginId, catalogEntries, current);
            if (updateVersion is not null)
            {
                actions |= PluginInstallActions.Update;
            }
        }

        if (rollbackVersion is not null)
        {
            actions |= PluginInstallActions.Rollback;
        }

        if (state is PluginState.Disabled or PluginState.DisabledByError or PluginState.Incompatible)
        {
            actions |= PluginInstallActions.Enable;
        }
        else if (state is PluginState.Loaded or PluginState.EnabledPendingRestart)
        {
            actions |= PluginInstallActions.Disable;
        }

        return (actions, installVersion, updateVersion);
    }

    /// <summary>
    /// Selects the highest compatible version for a plugin, optionally only
    /// versions strictly newer than <paramref name="minimumExclusive"/>. Merged
    /// sources can list the same plugin more than once, so the highest wins.
    /// </summary>
    private static string? SelectBestVersion(
        string pluginId,
        IReadOnlyList<CatalogEntry> catalogEntries,
        SemanticVersion? minimumExclusive)
    {
        string? bestText = null;
        SemanticVersion? best = null;
        foreach (var entry in catalogEntries)
        {
            if (entry.Compatibility == CatalogCompatibility.Incompatible ||
                !string.Equals(entry.Plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase) ||
                !SemanticVersion.TryParse(entry.Version.Version, out var candidate) ||
                (minimumExclusive is not null && candidate <= minimumExclusive))
            {
                continue;
            }

            if (best is null || candidate > best)
            {
                best = candidate;
                bestText = entry.Version.Version;
            }
        }

        return bestText;
    }
}
