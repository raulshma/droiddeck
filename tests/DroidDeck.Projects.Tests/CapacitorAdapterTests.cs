using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class CapacitorAdapterTests
{
    [Theory]
    [InlineData(null, "debug")]
    [InlineData("", "debug")]
    [InlineData("Debug", "debug")]
    [InlineData("RELEASE", "release")]
    public void NormalizeMode_FallsBackToDebug(string? mode, string expected)
    {
        Assert.Equal(expected, CapacitorAdapter.NormalizeMode(mode));
    }

    [Fact]
    public void GetBuildTask_CapitalizesTheMode()
    {
        Assert.Equal("assembleDebug", CapacitorAdapter.GetBuildTask(null));
        Assert.Equal("assembleRelease", CapacitorAdapter.GetBuildTask("release"));
    }

    [Fact]
    public void GetArtifactGlob_PointsAtTheAndroidHostOutput()
    {
        Assert.Equal(
            "android/app/build/outputs/apk/debug/*.apk",
            CapacitorAdapter.GetArtifactGlob("debug"));
        Assert.Equal(
            "android/app/build/outputs/apk/release/*.apk",
            CapacitorAdapter.GetArtifactGlob("release"));
    }

    [Fact]
    public void CreateSyncArguments_SyncsTheAndroidPlatformThroughTheCli()
    {
        Assert.Equal(["cap", "sync", "android"], CapacitorAdapter.CreateSyncArguments());
    }

    [Fact]
    public void CreateDefaultRunConfigurations_BuildsTheSyncedAndroidHost()
    {
        var project = new ProjectModel
        {
            ApplicationId = "com.example.capacitorapp",
            LauncherActivity = ".MainActivity",
        };

        var configurations = CapacitorAdapter.CreateDefaultRunConfigurations(project);

        Assert.Equal(["debug", "release"], configurations.Select(configuration => configuration.VariantName));
        Assert.All(configurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.Capacitor, configuration.Kind);
            Assert.Equal("android", configuration.Build.WorkingDirectory);
            Assert.Equal("com.example.capacitorapp", configuration.Launch.PackageName);
            Assert.Equal(".MainActivity", configuration.Launch.Activity);
        });
        Assert.Single(configurations, configuration => configuration.IsDefault && configuration.VariantName == "debug");
        Assert.Equal("assembleDebug", configurations[0].Build.Task);
        Assert.Equal(
            [
                "android/app/build/outputs/apk/debug/*.apk",
                "android/app/build/outputs/apk/release/*.apk",
            ],
            configurations.Select(configuration => configuration.Artifact.Glob));
    }

    [Fact]
    public void CreateVariants_ListsTheDebugAndReleaseModes()
    {
        var variants = CapacitorAdapter.CreateVariants();

        Assert.Equal(["debug", "release"], variants.Select(variant => variant.Name));
        Assert.True(variants[0].IsDefault);
        Assert.Equal("android/app/build/outputs/apk/debug/*.apk", variants[0].ArtifactGlob);
    }
}
