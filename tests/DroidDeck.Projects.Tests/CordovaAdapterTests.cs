using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class CordovaAdapterTests
{
    [Theory]
    [InlineData(null, "debug")]
    [InlineData("", "debug")]
    [InlineData("Debug", "debug")]
    [InlineData("RELEASE", "release")]
    public void NormalizeMode_FallsBackToDebug(string? mode, string expected)
    {
        Assert.Equal(expected, CordovaAdapter.NormalizeMode(mode));
    }

    [Fact]
    public void GetBuildTask_CapitalizesTheMode()
    {
        Assert.Equal("assembleDebug", CordovaAdapter.GetBuildTask(null));
        Assert.Equal("assembleRelease", CordovaAdapter.GetBuildTask("release"));
    }

    [Fact]
    public void GetArtifactGlob_PointsAtTheGeneratedAndroidPlatform()
    {
        Assert.Equal(
            "platforms/android/app/build/outputs/apk/debug/*.apk",
            CordovaAdapter.GetArtifactGlob("debug"));
        Assert.Equal(
            "platforms/android/app/build/outputs/apk/release/*.apk",
            CordovaAdapter.GetArtifactGlob("release"));
    }

    [Fact]
    public void CreatePrepareArguments_PreparesTheAndroidPlatformThroughTheCli()
    {
        Assert.Equal(["cordova", "prepare", "android"], CordovaAdapter.CreatePrepareArguments());
    }

    [Fact]
    public void CreateDefaultRunConfigurations_BuildsThePreparedAndroidPlatform()
    {
        var project = new ProjectModel
        {
            ApplicationId = "com.example.cordovaapp",
            LauncherActivity = ".MainActivity",
        };

        var configurations = CordovaAdapter.CreateDefaultRunConfigurations(project);

        Assert.Equal(["debug", "release"], configurations.Select(configuration => configuration.VariantName));
        Assert.All(configurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.Cordova, configuration.Kind);
            Assert.Equal("platforms/android", configuration.Build.WorkingDirectory);
            Assert.Equal("com.example.cordovaapp", configuration.Launch.PackageName);
        });
        Assert.Single(configurations, configuration => configuration.IsDefault && configuration.VariantName == "debug");
        Assert.Equal("assembleDebug", configurations[0].Build.Task);
        Assert.Equal(
            "platforms/android/app/build/outputs/apk/debug/*.apk",
            configurations[0].Artifact.Glob);
    }

    [Fact]
    public void CreateVariants_ListsTheDebugAndReleaseModes()
    {
        var variants = CordovaAdapter.CreateVariants();

        Assert.Equal(["debug", "release"], variants.Select(variant => variant.Name));
        Assert.True(variants[0].IsDefault);
        Assert.Equal("platforms/android/app/build/outputs/apk/debug/*.apk", variants[0].ArtifactGlob);
    }
}
