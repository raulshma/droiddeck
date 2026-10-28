using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class ReactNativeAdapterTests
{
    [Theory]
    [InlineData(null, "debug")]
    [InlineData("", "debug")]
    [InlineData("Debug", "debug")]
    [InlineData("RELEASE", "release")]
    [InlineData("profile", "debug")]
    public void NormalizeMode_FallsBackToDebug(string? mode, string expected)
    {
        Assert.Equal(expected, ReactNativeAdapter.NormalizeMode(mode));
    }

    [Fact]
    public void GetBuildTask_CapitalizesTheMode()
    {
        Assert.Equal("assembleDebug", ReactNativeAdapter.GetBuildTask(null));
        Assert.Equal("assembleRelease", ReactNativeAdapter.GetBuildTask("release"));
    }

    [Fact]
    public void GetArtifactGlob_PointsAtTheAndroidHostOutput()
    {
        Assert.Equal(
            "android/app/build/outputs/apk/debug/*.apk",
            ReactNativeAdapter.GetArtifactGlob("debug"));
        Assert.Equal(
            "android/app/build/outputs/apk/release/*.apk",
            ReactNativeAdapter.GetArtifactGlob("release"));
    }

    [Fact]
    public void CreateMetroStartArguments_StartsReactNativeOnThePort()
    {
        Assert.Equal(
            ["react-native", "start", "--port", "8081"],
            ReactNativeAdapter.CreateMetroStartArguments(isExpo: false, 8081));
        Assert.Equal(
            ["react-native", "start", "--port", "9090", "--reset-cache"],
            ReactNativeAdapter.CreateMetroStartArguments(isExpo: false, 9090, ["--reset-cache"]));
    }

    [Fact]
    public void CreateMetroStartArguments_UsesTheExpoDevClientForExpoProjects()
    {
        Assert.Equal(
            ["expo", "start", "--dev-client", "--port", "8081"],
            ReactNativeAdapter.CreateMetroStartArguments(isExpo: true, 8081));
    }

    [Fact]
    public void CreateDefaultRunConfigurations_BuildThroughTheAndroidHost()
    {
        var project = new ProjectModel
        {
            ApplicationId = "com.example.app",
            LauncherActivity = ".MainActivity",
        };

        var configurations = ReactNativeAdapter.CreateDefaultRunConfigurations(project);

        Assert.Equal(["debug", "release"], configurations.Select(configuration => configuration.VariantName));
        Assert.All(configurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.ReactNative, configuration.Kind);
            Assert.Equal("android", configuration.Build.WorkingDirectory);
            Assert.Equal("com.example.app", configuration.Launch.PackageName);
            Assert.Equal(".MainActivity", configuration.Launch.Activity);
        });
        Assert.Single(configurations, configuration => configuration.IsDefault && configuration.VariantName == "debug");
        Assert.Equal("assembleDebug", configurations[0].Build.Task);
        Assert.Equal("assembleRelease", configurations[1].Build.Task);
        Assert.Equal(
            [
                "android/app/build/outputs/apk/debug/*.apk",
                "android/app/build/outputs/apk/release/*.apk",
            ],
            configurations.Select(configuration => configuration.Artifact.Glob));
    }
}
