using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class MauiAdapterTests
{
    [Theory]
    [InlineData(null, "Debug")]
    [InlineData("", "Debug")]
    [InlineData("debug", "Debug")]
    [InlineData("RELEASE", "Release")]
    [InlineData("Staging", "Debug")]
    public void NormalizeConfiguration_FallsBackToDebug(string? configuration, string expected)
    {
        Assert.Equal(expected, MauiAdapter.NormalizeConfiguration(configuration));
    }

    [Fact]
    public void NormalizeTargetFramework_FallsBackToTheDefaultAndroidFramework()
    {
        Assert.Equal(MauiAdapter.DefaultTargetFramework, MauiAdapter.NormalizeTargetFramework(null));
        Assert.Equal(MauiAdapter.DefaultTargetFramework, MauiAdapter.NormalizeTargetFramework("  "));
        Assert.Equal("net9.0-android", MauiAdapter.NormalizeTargetFramework("net9.0-android"));
    }

    [Fact]
    public void CreateBuildArguments_BuildsTheSelectedConfigurationAndFramework()
    {
        Assert.Equal(
            ["build", "MauiApp/MauiApp.csproj", "-c", "Debug", "-f", "net9.0-android"],
            MauiAdapter.CreateBuildArguments("MauiApp/MauiApp.csproj", "Debug", "net9.0-android"));

        Assert.Equal(
            ["build", "MauiApp.csproj", "-c", "Release", "-f", "net10.0-android", "--no-restore", "/p:AndroidPackageFormats=apk"],
            MauiAdapter.CreateBuildArguments(
                "MauiApp.csproj",
                "release",
                "net10.0-android",
                ["--no-restore", "/p:AndroidPackageFormats=apk"]));
    }

    [Fact]
    public void GetArtifactGlob_PointsAtTheSignedApkNextToTheProjectFile()
    {
        Assert.Equal(
            "bin/Debug/net9.0-android/**/*-Signed.apk",
            MauiAdapter.GetArtifactGlob(null, "Debug", "net9.0-android"));
        Assert.Equal(
            "MauiApp/bin/Release/net10.0-android/**/*-Signed.apk",
            MauiAdapter.GetArtifactGlob("MauiApp", "Release", "net10.0-android"));
    }

    [Fact]
    public void CreateDefaultRunConfigurations_CoversEveryConfigurationAndAndroidFramework()
    {
        var project = new ProjectModel
        {
            ApplicationId = "com.example.mauiapp",
            LauncherActivity = "com.example.mauiapp.MainActivity",
        };
        var info = new MauiProjectInfo
        {
            ProjectFile = "MauiApp/MauiApp.csproj",
            TargetFrameworks = ["net9.0-android", "net9.0-ios"],
            AndroidTargetFrameworks = ["net9.0-android"],
        };

        var configurations = MauiAdapter.CreateDefaultRunConfigurations(project, info);

        Assert.Equal(["Debug", "Release"], configurations.Select(configuration => configuration.VariantName));
        Assert.All(configurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.Maui, configuration.Kind);
            Assert.Equal("net9.0-android", configuration.Build.TargetFramework);
            Assert.Equal("MauiApp", configuration.Build.WorkingDirectory);
            Assert.Equal("MauiApp.csproj", configuration.Build.Task);
            Assert.Equal("com.example.mauiapp", configuration.Launch.PackageName);
            Assert.Equal("com.example.mauiapp.MainActivity", configuration.Launch.Activity);
            Assert.Equal("MauiApp/bin/" + (configuration.VariantName == "Debug" ? "Debug" : "Release") +
                         "/net9.0-android/**/*-Signed.apk", configuration.Artifact.Glob);
        });
        Assert.Single(configurations, configuration => configuration.IsDefault);
        Assert.True(configurations[0].IsDefault);
        Assert.Equal("Debug", configurations[0].Name);
    }

    [Fact]
    public void CreateDefaultRunConfigurations_CreatesOneConfigurationPerFramework()
    {
        var info = new MauiProjectInfo
        {
            ProjectFile = "App.csproj",
            TargetFrameworks = ["net9.0-android35.0", "net9.0-android"],
            AndroidTargetFrameworks = ["net9.0-android", "net9.0-android35.0"],
        };

        var configurations = MauiAdapter.CreateDefaultRunConfigurations(new ProjectModel(), info);

        Assert.Equal(
            ["Debug (net9.0-android)", "Debug (net9.0-android35.0)", "Release (net9.0-android)", "Release (net9.0-android35.0)"],
            configurations.Select(configuration => configuration.Name));
    }

    [Fact]
    public void CreateVariants_MatchesTheDefaultConfigurations()
    {
        var info = new MauiProjectInfo
        {
            ProjectFile = "App.csproj",
            TargetFrameworks = ["net9.0-android"],
            AndroidTargetFrameworks = ["net9.0-android"],
        };

        var variants = MauiAdapter.CreateVariants(info);

        Assert.Equal(["Debug", "Release"], variants.Select(variant => variant.Name));
        Assert.All(variants, variant =>
        {
            Assert.Equal(DetectionSource.Detected, variant.Source);
            Assert.Equal("bin/" + (variant.Name == "Debug" ? "Debug" : "Release") +
                         "/net9.0-android/**/*-Signed.apk", variant.ArtifactGlob);
        });
        Assert.True(variants[0].IsDefault);
        Assert.False(variants[1].IsDefault);
    }
}
