using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class WebHybridAdapterTests
{
    [Fact]
    public void Adapter_UsesTheProfileForModesTasksAndGlobs()
    {
        var adapter = new WebHybridAdapter(CreateProfile());

        Assert.Equal(["debug", "release"], adapter.Modes);
        Assert.Equal("release", WebHybridAdapter.NormalizeMode(" RELEASE "));
        Assert.Equal("debug", WebHybridAdapter.NormalizeMode("profile"));
        Assert.Equal("assembleDebug", WebHybridAdapter.GetBuildTask(null));
        Assert.Equal("assembleRelease", WebHybridAdapter.GetBuildTask("release"));
        Assert.Equal(
            "platforms/android/app/build/outputs/apk/release/*.apk",
            adapter.GetArtifactGlob("release"));
        Assert.Equal(["demo", "prepare", "android"], adapter.CreatePrepareArguments());
    }

    [Fact]
    public void Adapter_BuildsRunConfigurationsFromTheProfile()
    {
        var adapter = new WebHybridAdapter(CreateProfile());
        var project = new ProjectModel
        {
            ApplicationId = "com.example.demo",
            LauncherActivity = ".MainActivity",
        };

        var configurations = adapter.CreateDefaultRunConfigurations(project);

        Assert.Equal(["debug", "release"], configurations.Select(configuration => configuration.VariantName));
        Assert.Equal(["demo-debug", "demo-release"], configurations.Select(configuration => configuration.Id));
        Assert.All(configurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.Cordova, configuration.Kind);
            Assert.Equal("platforms/android", configuration.Build.WorkingDirectory);
            Assert.Equal(1_200, configuration.Build.TimeoutSeconds);
            Assert.Equal("com.example.demo", configuration.Artifact.PackageName);
            Assert.Equal("com.example.demo", configuration.Launch.PackageName);
            Assert.Equal(".MainActivity", configuration.Launch.Activity);
        });
        Assert.True(configurations[0].IsDefault);
        Assert.False(configurations[1].IsDefault);
    }

    [Fact]
    public void Adapter_BuildsVariantsFromTheProfile()
    {
        var adapter = new WebHybridAdapter(CreateProfile());

        var variants = adapter.CreateVariants();

        Assert.Equal(["debug", "release"], variants.Select(variant => variant.Name));
        Assert.True(variants[0].IsDefault);
        Assert.Equal(DetectionSource.Detected, variants[0].Source);
        Assert.Equal("platforms/android/app/build/outputs/apk/debug/*.apk", variants[0].ArtifactGlob);
    }

    [Fact]
    public void CapacitorFacade_CapturesTheCapacitorProfile()
    {
        var profile = CapacitorAdapter.Shared.Profile;

        Assert.Equal(ProjectKind.Capacitor, profile.Kind);
        Assert.Equal(RunConfigurationKind.Capacitor, profile.RunConfigurationKind);
        Assert.Equal("capacitor", profile.IdPrefix);
        Assert.Equal(CapacitorAdapter.AndroidHostFolder, profile.AndroidHostFolder);
        Assert.Equal(CapacitorAdapter.ArtifactFolder, profile.ArtifactFolder);
        Assert.Equal(["debug", "release"], profile.Modes);
        Assert.Equal(["cap", "sync", "android"], profile.PrepareArguments);
    }

    [Fact]
    public void CordovaFacade_CapturesTheCordovaProfile()
    {
        var profile = CordovaAdapter.Shared.Profile;

        Assert.Equal(ProjectKind.Cordova, profile.Kind);
        Assert.Equal(RunConfigurationKind.Cordova, profile.RunConfigurationKind);
        Assert.Equal("cordova", profile.IdPrefix);
        Assert.Equal(CordovaAdapter.AndroidHostFolder, profile.AndroidHostFolder);
        Assert.Equal(CordovaAdapter.ArtifactFolder, profile.ArtifactFolder);
        Assert.Equal(["debug", "release"], profile.Modes);
        Assert.Equal(["cordova", "prepare", "android"], profile.PrepareArguments);
    }

    private static WebHybridProfile CreateProfile() => new(
        ProjectKind.Cordova,
        RunConfigurationKind.Cordova,
        "demo",
        "platforms/android",
        "platforms/android/app/build/outputs/apk",
        ["debug", "release"],
        ["demo", "prepare", "android"]);
}
