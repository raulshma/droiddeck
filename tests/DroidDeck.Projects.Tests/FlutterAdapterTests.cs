using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class FlutterAdapterTests
{
    [Theory]
    [InlineData(null, "debug")]
    [InlineData("", "debug")]
    [InlineData("Debug", "debug")]
    [InlineData("PROFILE", "profile")]
    [InlineData("release", "release")]
    [InlineData("custom", "debug")]
    public void NormalizeMode_FallsBackToDebug(string? mode, string expected)
    {
        Assert.Equal(expected, FlutterAdapter.NormalizeMode(mode));
    }

    [Fact]
    public void CreateBuildArguments_UsesTheSelectedModeAndExtraArguments()
    {
        var arguments = FlutterAdapter.CreateBuildArguments("profile", ["--dart-define=API=test"]);

        Assert.Equal(["build", "apk", "--profile", "--dart-define=API=test"], arguments);
        Assert.Equal(["build", "apk", "--debug"], FlutterAdapter.CreateBuildArguments(null, null));
    }

    [Fact]
    public void CreateRunSessionArguments_EnablesMachineModeForTheDevice()
    {
        var arguments = FlutterAdapter.CreateRunSessionArguments("emulator-5554", "debug", ["--dart-define=A=1"]);

        Assert.Equal(
            ["run", "--machine", "-d", "emulator-5554", "--debug", "--dart-define=A=1"],
            arguments);
    }

    [Fact]
    public void CreateTestArguments_RequestsMachineReadableResults()
    {
        Assert.Equal(["test", "--reporter", "json"], FlutterAdapter.CreateTestArguments());
    }

    [Fact]
    public void CreateDefaultRunConfigurations_ExposeDebugProfileAndRelease()
    {
        var project = new ProjectModel
        {
            ApplicationId = "com.example.app",
            LauncherActivity = ".MainActivity",
        };

        var configurations = FlutterAdapter.CreateDefaultRunConfigurations(project);

        Assert.Equal(["debug", "profile", "release"], configurations.Select(configuration => configuration.VariantName));
        Assert.All(configurations, configuration => Assert.Equal(RunConfigurationKind.Flutter, configuration.Kind));
        Assert.Single(configurations, configuration => configuration.IsDefault && configuration.VariantName == "debug");
        Assert.Equal(
            [
                "build/app/outputs/flutter-apk/app-debug.apk",
                "build/app/outputs/flutter-apk/app-profile.apk",
                "build/app/outputs/flutter-apk/app-release.apk",
            ],
            configurations.Select(configuration => configuration.Artifact.Glob));
        Assert.All(configurations, configuration =>
        {
            Assert.Equal("com.example.app", configuration.Launch.PackageName);
            Assert.Equal(".MainActivity", configuration.Launch.Activity);
            Assert.Null(configuration.Build.Task);
        });
    }
}
