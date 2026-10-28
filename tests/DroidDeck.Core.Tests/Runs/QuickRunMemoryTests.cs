using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Runs;

public sealed class QuickRunMemoryTests
{
    [Fact]
    public async Task Resolve_ReturnsTheRememberedProjectConfigurationAndDevice()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        var memory = new QuickRunMemory(configuration);
        await memory.RememberAsync("shop-app", "shop-debug", "PIXEL-8");

        var selection = memory.Resolve(
            [CreateProject("my-app", "my-app-debug"), CreateProject("shop-app", "shop-debug")],
            [CreateDevice("POCO-F1"), CreateDevice("PIXEL-8")]);

        Assert.Equal("shop-app", selection.Project?.Id);
        Assert.Equal("shop-debug", selection.Configuration?.Id);
        Assert.Equal("PIXEL-8", selection.Device?.Serial);
        Assert.True(selection.CanRun);
        Assert.Equal(QuickRunBlock.None, selection.Block);
    }

    [Fact]
    public async Task Resolve_FallsBackToTheMostRecentlyOpenedProjectAndItsDefaultConfiguration()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        var memory = new QuickRunMemory(configuration);
        var idle = CreateProject("idle-app", "idle-debug");
        idle.LastOpenedAt = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var recent = CreateProject("recent-app", "recent-debug");
        recent.LastOpenedAt = new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        recent.RunConfigurations.Add(new RunConfiguration
        {
            Id = "recent-release",
            Name = "recent-release",
            Kind = RunConfigurationKind.Gradle,
            Build = new BuildConfiguration { Task = "assembleRelease" },
            Artifact = new ArtifactConfiguration { Glob = "app/build/outputs/apk/release/*.apk" },
            IsDefault = true,
        });
        recent.RunConfigurations[0].IsDefault = false;

        var selection = memory.Resolve([idle, recent], [CreateDevice("POCO-F1"), CreateDevice("PIXEL-8")]);

        Assert.Equal("recent-app", selection.Project?.Id);
        Assert.Equal("recent-release", selection.Configuration?.Id);
        Assert.Equal("POCO-F1", selection.Device?.Serial);
        Assert.True(selection.CanRun);
    }

    [Fact]
    public async Task Resolve_IgnoresRememberedIdentifiersThatNoLongerExist()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        var memory = new QuickRunMemory(configuration);
        await memory.RememberAsync("deleted-app", "deleted-config", "GONE-1");

        var selection = memory.Resolve([CreateProject("my-app", "my-app-debug")], [CreateDevice("PIXEL-8")]);

        Assert.Equal("my-app", selection.Project?.Id);
        Assert.Equal("my-app-debug", selection.Configuration?.Id);
        Assert.Equal("PIXEL-8", selection.Device?.Serial);
        Assert.True(selection.CanRun);
    }

    [Fact]
    public async Task Resolve_ReportsNoReadyDevicesWhenEveryDeviceNeedsAttention()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        var memory = new QuickRunMemory(configuration);

        var selection = memory.Resolve(
            [CreateProject("my-app", "my-app-debug")],
            [CreateDevice("POCO-F1", DeviceState.Unauthorized), CreateDevice("EMULATOR-5554", DeviceState.Offline)]);

        Assert.Equal(QuickRunBlock.NoReadyDevices, selection.Block);
        Assert.False(selection.CanRun);
        Assert.Equal("my-app", selection.Project?.Id);
        Assert.Equal("my-app-debug", selection.Configuration?.Id);
        Assert.Null(selection.Device);
    }

    [Fact]
    public async Task Resolve_PrefersAReadyDeviceOverARememberedDeviceThatIsNotReady()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        var memory = new QuickRunMemory(configuration);
        await memory.RememberAsync("my-app", "my-app-debug", "POCO-F1");

        var selection = memory.Resolve(
            [CreateProject("my-app", "my-app-debug")],
            [CreateDevice("POCO-F1", DeviceState.Unauthorized), CreateDevice("PIXEL-8")]);

        Assert.Equal("PIXEL-8", selection.Device?.Serial);
        Assert.True(selection.CanRun);
    }

    [Fact]
    public async Task Resolve_ReportsNoProjectsWhenNothingHasBeenAdded()
    {
        using var temp = new TempDirectory();
        using var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        var memory = new QuickRunMemory(configuration);

        var selection = memory.Resolve([], [CreateDevice("PIXEL-8")]);

        Assert.Equal(QuickRunBlock.NoProjects, selection.Block);
        Assert.False(selection.CanRun);
        Assert.Null(selection.Project);
    }

    [Fact]
    public async Task RememberAsync_RestoresTheTargetAfterARestart()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using (var configuration = new ConfigurationService(paths))
        {
            await configuration.InitializeAsync();
            await new QuickRunMemory(configuration).RememberAsync("shop-app", "shop-release", "PIXEL-8");
        }

        var shop = CreateProject("shop-app", "shop-debug");
        shop.RunConfigurations.Add(new RunConfiguration
        {
            Id = "shop-release",
            Name = "shop-release",
            Kind = RunConfigurationKind.Gradle,
            Build = new BuildConfiguration { Task = "assembleRelease" },
            Artifact = new ArtifactConfiguration { Glob = "app/build/outputs/apk/release/*.apk" },
        });
        using var reopened = new ConfigurationService(paths);
        await reopened.InitializeAsync();

        var selection = new QuickRunMemory(reopened).Resolve(
            [CreateProject("my-app", "my-app-debug"), shop],
            [CreateDevice("POCO-F1"), CreateDevice("PIXEL-8")]);

        Assert.Equal("shop-app", selection.Project?.Id);
        Assert.Equal("shop-release", selection.Configuration?.Id);
        Assert.Equal("PIXEL-8", selection.Device?.Serial);
    }

    private static ProjectModel CreateProject(string id, string configurationId) => new()
    {
        Id = id,
        Name = id,
        RootPath = Path.Combine(Path.GetTempPath(), id),
        RunConfigurations =
        [
            new RunConfiguration
            {
                Id = configurationId,
                Name = configurationId,
                Kind = RunConfigurationKind.Gradle,
                Build = new BuildConfiguration { Task = "assembleDebug" },
                Artifact = new ArtifactConfiguration { Glob = "app/build/outputs/apk/debug/*.apk" },
                IsDefault = true,
            },
        ],
    };

    private static DeviceInfo CreateDevice(string serial, DeviceState state = DeviceState.Device) => new()
    {
        Serial = serial,
        State = state,
        Model = serial,
    };
}
