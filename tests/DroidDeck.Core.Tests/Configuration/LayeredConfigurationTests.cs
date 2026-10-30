using System.Diagnostics;
using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Projects;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class LayeredConfigurationTests
{
    private const string MaxSizePath = SettingsPaths.ScreenMaxSize;

    public static TheoryData<ConfigScope> SingleLayerOverridesWin()
        => new()
        {
            ConfigScope.Global,
            ConfigScope.TeamProject,
            ConfigScope.LocalProject,
            ConfigScope.RunConfiguration,
            ConfigScope.Session,
        };

    [Theory]
    [MemberData(nameof(SingleLayerOverridesWin))]
    public async Task SingleLayerOverrideWins(ConfigScope scope)
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetProjectContextAsync(new ProjectContext(temp.Combine("repo"), "proj-1"));

        await SetAtScopeAsync(service, scope, 720);

        Assert.Equal(720, service.GetValue<int>(MaxSizePath));
        Assert.Equal(scope, service.GetSource(MaxSizePath).Scope);
        Assert.True(service.Current.IsOverridden(MaxSizePath));
    }

    [Fact]
    public async Task PrecedenceFollowsTheEightScopeOrder()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        Assert.Equal(1024, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(MaxSizePath).Scope);

        await service.SetAsync(MaxSizePath, 1280);
        await WriteProfileDocument(temp.Combine("data", "profiles", "team.json"), MaxSizePath, JsonValue.Create(1111));
        await service.UseProfileAsync("team");

        var repo = temp.Combine("repo");
        await service.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));
        await service.SetAsync(MaxSizePath, 2222, ConfigScope.TeamProject);
        await service.SetAsync(MaxSizePath, 3333, ConfigScope.LocalProject);
        await service.SetAsync(MaxSizePath, 4444, ConfigScope.RunConfiguration);
        await service.SetDeviceOverrideAsync(MaxSizePath, 5555, DeviceOverrideTarget.Group("builders"));
        await service.SetDeviceOverrideAsync(MaxSizePath, 6666, DeviceOverrideTarget.Serial("emu-5554"));
        await service.SetAsync(MaxSizePath, 7777, ConfigScope.Session);

        Assert.Equal(7777, service.GetValue<int>(MaxSizePath));

        await service.ResetAsync(MaxSizePath, ConfigScope.Session);
        Assert.Equal(6666, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.Device, service.GetSource(MaxSizePath).Scope);
        Assert.Equal("serial:emu-5554", service.GetSource(MaxSizePath).Origin);

        await service.ResetDeviceOverrideAsync(MaxSizePath, DeviceOverrideTarget.Serial("emu-5554"));
        Assert.Equal(5555, service.GetValue<int>(MaxSizePath));
        Assert.Equal("group:builders", service.GetSource(MaxSizePath).Origin);

        await service.ResetDeviceOverrideAsync(MaxSizePath, DeviceOverrideTarget.Group("builders"));
        Assert.Equal(4444, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.RunConfiguration, service.GetSource(MaxSizePath).Scope);

        await service.ResetAsync(MaxSizePath, ConfigScope.RunConfiguration);
        Assert.Equal(3333, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.LocalProject, service.GetSource(MaxSizePath).Scope);

        await service.ResetAsync(MaxSizePath, ConfigScope.LocalProject);
        Assert.Equal(2222, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.TeamProject, service.GetSource(MaxSizePath).Scope);

        await service.ResetAsync(MaxSizePath, ConfigScope.TeamProject);
        Assert.Equal(1111, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.Profile, service.GetSource(MaxSizePath).Scope);

        await service.UseProfileAsync(null);
        Assert.Equal(1280, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.Global, service.GetSource(MaxSizePath).Scope);

        await service.ResetAsync(MaxSizePath);
        Assert.Equal(1024, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(MaxSizePath).Scope);
    }

    [Fact]
    public async Task SourceMapExposesContributionsFromLowestToHighest()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetProjectContextAsync(new ProjectContext(temp.Combine("repo"), "proj-1"));

        await service.SetAsync(MaxSizePath, 1280);
        await service.SetAsync(MaxSizePath, 720, ConfigScope.LocalProject);
        await service.SetAsync(MaxSizePath, 480, ConfigScope.Session);

        var snapshot = service.Current;
        var contributions = snapshot.GetContributions(MaxSizePath);

        Assert.Equal(3, contributions.Count);
        Assert.Equal(ConfigScope.Global, contributions[0].Scope);
        Assert.Equal(ConfigScope.LocalProject, contributions[1].Scope);
        Assert.Equal(ConfigScope.Session, contributions[2].Scope);
        Assert.Equal(contributions[^1], snapshot.GetSource(MaxSizePath));

        Assert.Empty(snapshot.GetContributions(SettingsPaths.Theme));
        Assert.Equal(ConfigSource.Defaults, snapshot.GetSource(SettingsPaths.Theme));
    }

    [Fact]
    public async Task DeviceSpecificityIsSerialOverGroupOverAll()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        await service.SetDeviceOverrideAsync(MaxSizePath, 111, DeviceOverrideTarget.AllDevices);
        Assert.Equal(111, service.GetValue<int>(MaxSizePath));
        Assert.Equal("all-devices", service.GetSource(MaxSizePath).Origin);

        await service.SetDeviceOverrideAsync(MaxSizePath, 222, DeviceOverrideTarget.Group("perf"));
        Assert.Equal(222, service.GetValue<int>(MaxSizePath));
        Assert.Equal("group:perf", service.GetSource(MaxSizePath).Origin);

        await service.SetDeviceOverrideAsync(MaxSizePath, 333, DeviceOverrideTarget.Serial("R58N"));
        Assert.Equal(333, service.GetValue<int>(MaxSizePath));
        Assert.Equal("serial:R58N", service.GetSource(MaxSizePath).Origin);

        await service.ResetDeviceOverrideAsync(MaxSizePath, DeviceOverrideTarget.Serial("R58N"));
        await service.ResetDeviceOverrideAsync(MaxSizePath, DeviceOverrideTarget.Group("perf"));
        Assert.Equal(111, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.Device, service.GetSource(MaxSizePath).Scope);
    }

    [Fact]
    public async Task TeamSharedConfigOptInRequiresProjectContext()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetAsync(MaxSizePath, 720, ConfigScope.TeamProject));
    }

    [Fact]
    public async Task LocalProjectConfigPersistsAcrossRestart()
    {
        using var temp = new TempDirectory();
        var repo = temp.Combine("repo");
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await service.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));
            await service.SetAsync(MaxSizePath, 720, ConfigScope.LocalProject);
        }

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();
        await restarted.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));

        Assert.Equal(720, restarted.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.LocalProject, restarted.GetSource(MaxSizePath).Scope);
    }

    [Fact]
    public async Task TeamSharedConfigPersistsAcrossRestart()
    {
        using var temp = new TempDirectory();
        var repo = temp.Combine("repo");
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await service.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));
            await service.SetAsync(MaxSizePath, 720, ConfigScope.TeamProject);
        }

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();
        await restarted.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));

        Assert.Equal(720, restarted.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.TeamProject, restarted.GetSource(MaxSizePath).Scope);
        Assert.True(File.Exists(Path.Combine(repo, ".droiddeck", "project.json")));
        Assert.False(File.Exists(Path.Combine(repo, ".droiddeck", "project.json.bak")));
    }

    [Fact]
    public async Task ProfileLayerLoadsTheActiveProfileDocument()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await WriteProfileDocument(
            temp.Combine("data", "profiles", "demo.json"),
            SettingsPaths.Density,
            JsonValue.Create("compact"));

        Assert.Equal("comfortable", service.GetString(SettingsPaths.Density));

        await service.UseProfileAsync("demo");

        Assert.Equal("compact", service.GetString(SettingsPaths.Density));
        Assert.Equal(ConfigScope.Profile, service.GetSource(SettingsPaths.Density).Scope);

        await service.UseProfileAsync(null);

        Assert.Equal("comfortable", service.GetString(SettingsPaths.Density));
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(SettingsPaths.Density).Scope);
    }

    [Fact]
    public async Task ActiveProfileIdPersistsAcrossRestart()
    {
        using var temp = new TempDirectory();
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await WriteProfileDocument(
                temp.Combine("data", "profiles", "demo.json"),
                SettingsPaths.Density,
                JsonValue.Create("compact"));
            await service.UseProfileAsync("demo");
        }

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();

        Assert.Equal("demo", restarted.GetString("profile.activeId"));
        Assert.Equal("compact", restarted.GetString(SettingsPaths.Density));
        Assert.Equal(ConfigScope.Profile, restarted.GetSource(SettingsPaths.Density).Scope);
    }

    [Fact]
    public async Task ProfileLayerSupportsPerPathReset()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await WriteProfileDocument(
            temp.Combine("data", "profiles", "demo.json"),
            SettingsPaths.Density,
            JsonValue.Create("compact"));
        await service.UseProfileAsync("demo");

        Assert.Equal("compact", service.GetString(SettingsPaths.Density));

        await service.ResetAsync(SettingsPaths.Density, ConfigScope.Profile);

        Assert.Equal("comfortable", service.GetString(SettingsPaths.Density));
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(SettingsPaths.Density).Scope);
        Assert.True(File.Exists(temp.Combine("data", "profiles", "demo.json")));
    }

    [Fact]
    public async Task RunConfigurationLayerComposesWithTheRunConfigurationModel()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        var runConfiguration = new RunConfiguration
        {
            Id = "release",
            Name = "Release",
            Kind = RunConfigurationKind.Gradle,
            Screen = { Mode = ScreenMode.External, AutoOpen = true },
            Install = { GrantPermissions = true },
        };
        await service.SetRunConfigurationAsync(runConfiguration);

        Assert.Equal(ScreenMode.External, service.GetValue<ScreenMode>("screen.mode"));
        Assert.True(service.GetValue<bool>("screen.autoOpen"));
        Assert.True(service.GetValue<bool>("install.grantPermissions"));
        Assert.Equal(ConfigScope.RunConfiguration, service.GetSource("screen.mode").Scope);

        await service.SetRunConfigurationAsync(null);

        Assert.Equal(ScreenMode.Off, service.GetValue<ScreenMode>("screen.mode"));
        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource("screen.mode").Scope);
    }

    [Fact]
    public async Task SessionOverridesApplyAndClear()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(MaxSizePath, 1280);

        await service.SetAsync(MaxSizePath, 480, ConfigScope.Session);
        Assert.Equal(480, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.Session, service.GetSource(MaxSizePath).Scope);

        await service.ClearSessionOverridesAsync();

        Assert.Equal(1280, service.GetValue<int>(MaxSizePath));
        Assert.Equal(ConfigScope.Global, service.GetSource(MaxSizePath).Scope);
    }

    [Fact]
    public async Task SessionOverridesAreEphemeralAcrossRestart()
    {
        using var temp = new TempDirectory();
        using (var service = CreateService(temp))
        {
            await service.InitializeAsync();
            await service.SetAsync(MaxSizePath, 480, ConfigScope.Session);
        }

        using var restarted = CreateService(temp);
        await restarted.InitializeAsync();

        Assert.Equal(1024, restarted.GetValue<int>(MaxSizePath));
    }

    [Fact]
    public async Task CommandLineOverridesApplyToTheSessionLayer()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        var parsed = CliOverrides.ParseArguments(
        [
            "--unknown",
            "ignored",
            "--set",
            $"{MaxSizePath}=720",
            "--set",
            "logging.maxUiLines=5000",
            "--profile",
            "demo",
        ]);

        await service.SetSessionOverridesAsync(parsed.Settings);

        Assert.Equal(720, service.GetValue<int>(MaxSizePath));
        Assert.Equal(5000, service.GetValue<int>("logging.maxUiLines"));
        Assert.Equal(ConfigScope.Session, service.GetSource(MaxSizePath).Scope);
        Assert.Equal("demo", parsed.ProfileId);
    }

    [Fact]
    public async Task EnvironmentOverridesApplyToTheSessionLayer()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        var overrides = CliOverrides.ParseEnvironment(
        [
            new KeyValuePair<string, string?>("DROIDDECK__LOGGING__LEVEL", "Debug"),
            new KeyValuePair<string, string?>("DROIDDECK__SCREEN__MAXSIZE", "480"),
            new KeyValuePair<string, string?>("OTHER__IGNORED", "true"),
        ]);

        await service.SetSessionOverridesAsync(overrides);

        Assert.Equal("Debug", service.GetString("logging.level"));
        Assert.Equal(480, service.GetValue<int>(MaxSizePath));
    }

    [Fact]
    public async Task SessionProfileSelectionActivatesTheProfileLayer()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await WriteProfileDocument(
            temp.Combine("data", "profiles", "demo.json"),
            SettingsPaths.Density,
            JsonValue.Create("compact"));

        await service.SetAsync("profile.activeId", "demo", ConfigScope.Session);

        Assert.Equal("compact", service.GetString(SettingsPaths.Density));
        Assert.Equal(ConfigScope.Profile, service.GetSource(SettingsPaths.Density).Scope);
    }

    [Fact]
    public async Task ChangesPropagateToConsumersBeforeTheWriteReturns()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        ConfigChangedEventArgs? received = null;
        service.Changed += (_, args) => received = args;

        await service.SetAsync(MaxSizePath, 720);

        // No wall-clock assertion here: the call includes the durable file write, so
        // its duration tracks storage latency on the machine, not propagation.
        Assert.NotNull(received);
        Assert.Equal(MaxSizePath, received.Path);
        Assert.Equal(ConfigScope.Global, received.Scope);
        Assert.Equal(ConfigChangeKind.Set, received.Kind);
    }

    [Fact]
    public async Task InMemoryChangesPropagateWithinThe100MillisecondBudget()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(MaxSizePath, 480, ConfigScope.Session);
        ConfigChangedEventArgs? received = null;
        service.Changed += (_, args) => received = args;
        var stopwatch = Stopwatch.StartNew();

        await service.SetAsync(MaxSizePath, 720, ConfigScope.Session);

        stopwatch.Stop();
        Assert.NotNull(received);
        Assert.Equal(ConfigScope.Session, received.Scope);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Propagation took {stopwatch.Elapsed}; the budget covers in-memory propagation.");
    }

    [Fact]
    public async Task ChangeTokenSignalsTheNewSnapshot()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        var token = service.GetChangeToken();
        var notified = false;
        using var registration = token.RegisterChangeCallback((_) => notified = true, null);

        Assert.False(token.HasChanged);
        await service.SetAsync(MaxSizePath, 720);

        Assert.True(token.HasChanged);
        Assert.True(notified);
    }

    [Fact]
    public async Task CachedResolutionStaysWithinTheBudget()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetAsync(MaxSizePath, 720);
        var snapshot = service.Current;
        var stopwatch = Stopwatch.StartNew();

        for (var index = 0; index < 5000; index++)
        {
            _ = service.GetValue<int>(MaxSizePath);
            _ = service.GetString(SettingsPaths.Density);
            _ = service.GetSource(MaxSizePath);
            _ = snapshot.GetValue(MaxSizePath);
            _ = snapshot.GetContributions(MaxSizePath);
        }

        stopwatch.Stop();
        // The design target is well under 1 ms per cached resolution. The total
        // bound is generous because solution-wide test runs execute several
        // assemblies in parallel; 5 s still catches a multi-fold regression
        // while keeping the release gate stable under load.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"25,000 cached resolutions took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task ResetOnUnusedLayerStillReportsTheEffectiveSource()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        ConfigChangedEventArgs? change = null;
        service.Changed += (_, args) => change = args;

        await service.ResetAsync(SettingsPaths.Theme, ConfigScope.Session);

        Assert.Equal(ConfigScope.BuiltInDefaults, service.GetSource(SettingsPaths.Theme).Scope);
        Assert.NotNull(change);
        Assert.Equal(ConfigChangeKind.Reset, change.Kind);
        Assert.Equal(ConfigScope.Session, change.Scope);
    }

    [Fact]
    public async Task UnsupportedScopesAreRejected()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.SetAsync(SettingsPaths.Theme, AppTheme.Dark, ConfigScope.BuiltInDefaults));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.SetAsync(SettingsPaths.Theme, AppTheme.Dark, ConfigScope.Profile));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => service.SetAsync(SettingsPaths.Theme, AppTheme.Dark, ConfigScope.Device));
    }

    private static ConfigurationService CreateService(TempDirectory temp) => new(Paths(temp));

    private static AppPaths Paths(TempDirectory temp) => new(temp.Combine("data"));

    private static Task SetAtScopeAsync(ConfigurationService service, ConfigScope scope, int value)
        => scope switch
        {
            ConfigScope.Global => service.SetAsync(MaxSizePath, value),
            ConfigScope.TeamProject => service.SetAsync(MaxSizePath, value, ConfigScope.TeamProject),
            ConfigScope.LocalProject => service.SetAsync(MaxSizePath, value, ConfigScope.LocalProject),
            ConfigScope.RunConfiguration => service.SetAsync(MaxSizePath, value, ConfigScope.RunConfiguration),
            ConfigScope.Session => service.SetAsync(MaxSizePath, value, ConfigScope.Session),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

    private static async Task WriteProfileDocument(string file, string path, JsonNode value)
    {
        var segments = path.Split('.');
        var root = new JsonObject { ["schemaVersion"] = 1 };
        var current = root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var next = new JsonObject();
            current[segments[index]] = next;
            current = next;
        }

        current[segments[^1]] = value.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, root.ToJsonString());
    }
}
