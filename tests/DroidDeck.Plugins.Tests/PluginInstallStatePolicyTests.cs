using DroidDeck.Plugins.Catalog;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginInstallStatePolicyTests
{
    private const string PluginId = "com.example.chart";

    [Fact]
    public void APluginMissingFromTheHostCanBeInstalled()
    {
        var state = PluginInstallStatePolicy.EvaluateNotInstalled(
            PluginId,
            [Entry(PluginId, "1.0.0"), Entry(PluginId, "1.4.0"), Entry(PluginId, "1.2.0")]);

        Assert.Equal(PluginInstallActions.Install, state.Actions);
        Assert.True(state.CanInstall);
        Assert.Equal("1.4.0", state.InstallVersion);
        Assert.Null(state.UpdateVersion);
        Assert.False(state.CanUpdate);
        Assert.False(state.CanRollback);
        Assert.False(state.CanEnable);
        Assert.False(state.CanDisable);
    }

    [Fact]
    public void APluginWithoutACompatibleCatalogVersionCannotBeInstalled()
    {
        var state = PluginInstallStatePolicy.EvaluateNotInstalled(
            PluginId,
            [Entry(PluginId, "2.0.0", CatalogCompatibility.Incompatible)]);

        Assert.Equal(PluginInstallActions.None, state.Actions);
        Assert.Null(state.InstallVersion);
    }

    [Fact]
    public void AnInstalledPluginAtTheCatalogVersionHasNoUpdate()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Loaded),
            [Entry(PluginId, "1.0.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Disable, state.Actions);
        Assert.False(state.CanUpdate);
        Assert.Null(state.UpdateVersion);
        Assert.Equal(string.Empty, state.UpdateLabel);
    }

    [Fact]
    public void AnOlderInstalledPluginUpdatesToTheHighestCompatibleVersion()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Loaded),
            [
                Entry(PluginId, "1.1.0"),
                Entry(PluginId, "1.3.0", CatalogCompatibility.Incompatible),
                Entry(PluginId, "1.2.0"),
            ],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Update | PluginInstallActions.Disable, state.Actions);
        Assert.True(state.CanUpdate);
        Assert.Equal("1.2.0", state.UpdateVersion);
        Assert.Equal("Update to v1.2.0", state.UpdateLabel);
    }

    [Fact]
    public void AnInstalledPluginNewerThanEveryCatalogVersionHasNoUpdateButCanRollBack()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "2.0.0", PluginState.Loaded),
            [Entry(PluginId, "1.9.0")],
            rollbackVersion: "1.8.0");

        Assert.Equal(PluginInstallActions.Rollback | PluginInstallActions.Disable, state.Actions);
        Assert.False(state.CanUpdate);
        Assert.True(state.CanRollback);
        Assert.Equal("1.8.0", state.RollbackVersion);
        Assert.Equal("Roll back to v1.8.0", state.RollbackLabel);
    }

    [Fact]
    public void AnUpdateAndARollbackCanBeOfferedTogether()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Loaded),
            [Entry(PluginId, "1.1.0")],
            rollbackVersion: "0.9.0");

        Assert.Equal(
            PluginInstallActions.Update | PluginInstallActions.Rollback | PluginInstallActions.Disable,
            state.Actions);
        Assert.Equal("1.1.0", state.UpdateVersion);
        Assert.Equal("0.9.0", state.RollbackVersion);
    }

    [Theory]
    [InlineData(PluginState.Disabled)]
    [InlineData(PluginState.DisabledByError)]
    [InlineData(PluginState.Incompatible)]
    public void DisabledPluginsCanBeEnabled(PluginState pluginState)
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", pluginState, reason: "Disabled for testing."),
            [Entry(PluginId, "1.0.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Enable, state.Actions);
        Assert.True(state.CanEnable);
        Assert.False(state.CanDisable);
        Assert.Equal("Disabled for testing.", state.Reason);
    }

    [Fact]
    public void ADisabledPluginCanAlsoBeUpdated()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Disabled),
            [Entry(PluginId, "1.1.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Update | PluginInstallActions.Enable, state.Actions);
    }

    [Theory]
    [InlineData(PluginState.Loaded)]
    [InlineData(PluginState.EnabledPendingRestart)]
    public void LoadedPluginsCanBeDisabled(PluginState pluginState)
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", pluginState),
            [Entry(PluginId, "1.0.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Disable, state.Actions);
        Assert.True(state.CanDisable);
        Assert.False(state.CanEnable);
    }

    [Fact]
    public void APluginAwaitingTrustHasNoEnableOrDisableAction()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.TrustRequired),
            [Entry(PluginId, "1.0.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.None, state.Actions);
        Assert.False(state.CanEnable);
        Assert.False(state.CanDisable);
    }

    [Fact]
    public void AnUnparseableInstalledVersionHasNoUpdate()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "development", PluginState.Loaded),
            [Entry(PluginId, "99.0.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Disable, state.Actions);
        Assert.Null(state.UpdateVersion);
    }

    [Fact]
    public void AnUnparseableCatalogVersionIsIgnored()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Loaded),
            [Entry(PluginId, "development")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Disable, state.Actions);
    }

    [Fact]
    public void PluginIdsAreMatchedCaseInsensitively()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Loaded),
            [Entry("COM.EXAMPLE.CHART", "1.1.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Update | PluginInstallActions.Disable, state.Actions);
        Assert.Equal("1.1.0", state.UpdateVersion);
    }

    [Fact]
    public void CatalogEntriesForOtherPluginsAreIgnored()
    {
        var state = PluginInstallStatePolicy.Evaluate(
            Status(PluginId, "1.0.0", PluginState.Loaded),
            [Entry("com.example.other", "9.0.0")],
            rollbackVersion: null);

        Assert.Equal(PluginInstallActions.Disable, state.Actions);
    }

    [Fact]
    public void TheHighestVersionWinsAcrossMergedCatalogSources()
    {
        var state = PluginInstallStatePolicy.EvaluateNotInstalled(
            PluginId,
            [Entry(PluginId, "1.1.0", sourceId: "src-1"), Entry(PluginId, "1.2.0", sourceId: "src-2")]);

        Assert.Equal("1.2.0", state.InstallVersion);
    }

    [Fact]
    public void EvaluateAllComputesEveryPluginAndMatchesRollbacksCaseInsensitively()
    {
        var statuses = new[]
        {
            Status(PluginId, "1.0.0", PluginState.Loaded),
            Status("com.example.other", "2.0.0", PluginState.Disabled),
        };
        var catalog = new[] { Entry(PluginId, "1.1.0"), Entry("com.example.other", "2.0.0") };
        var rollback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["COM.EXAMPLE.CHART"] = "0.9.0",
            ["com.example.not-installed"] = "1.0.0",
        };

        var states = PluginInstallStatePolicy.EvaluateAll(statuses, catalog, rollback);

        Assert.Equal(2, states.Count);
        var chart = states["COM.EXAMPLE.CHART"];
        Assert.Equal(
            PluginInstallActions.Update | PluginInstallActions.Rollback | PluginInstallActions.Disable,
            chart.Actions);
        Assert.Equal("0.9.0", chart.RollbackVersion);
        var other = states["com.example.other"];
        Assert.Equal(PluginInstallActions.Enable, other.Actions);
    }

    private static PluginStatus Status(
        string pluginId,
        string version,
        PluginState state,
        string? reason = null) => new()
        {
            Id = pluginId,
            Name = "Chart Renderer",
            Version = version,
            ApiVersion = "1.0",
            InstallPath = $@"C:\plugins\{pluginId}",
            State = state,
            Reason = reason,
        };

    private static CatalogEntry Entry(
        string pluginId,
        string version,
        CatalogCompatibility compatibility = CatalogCompatibility.Compatible,
        string sourceId = "src-1") => new()
        {
            Source = new CatalogSource
            {
                Id = sourceId,
                Name = sourceId,
                Url = $"https://example.com/{sourceId}/catalog.json",
            },
            Plugin = new CatalogPlugin { Id = pluginId, Name = "Chart Renderer" },
            Version = new CatalogVersion
            {
                Version = version,
                ApiVersion = "1.0",
                DownloadUrl = $"https://example.com/{pluginId}-{version}.ddplugin",
                Sha256 = "hash",
            },
            Compatibility = compatibility,
            CompatibilityLabel = compatibility.ToString(),
            IsPrerelease = false,
        };
}
