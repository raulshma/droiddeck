using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class MauiProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsMauiProjectsWithRunConfigurations()
    {
        using var temp = new TempDirectory();
        WriteMauiProject(temp, "MauiSample/MauiSample.csproj", "MauiSample");
        var detector = new MauiProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Maui, project.Kind);
        Assert.Equal(".NET MAUI", project.KindLabel);
        Assert.Equal("Maui Sample", project.Name);
        Assert.Equal(DetectionConfidence.High, project.Confidence);
        Assert.Equal("com.example.mauiapp", project.ApplicationId);
        Assert.NotNull(project.MauiInfo);
        Assert.Equal("MauiSample/MauiSample.csproj", project.MauiInfo.ProjectFile);
        Assert.True(project.MauiInfo.SupportsAndroid);
        Assert.Equal(["net9.0-android"], project.MauiInfo.AndroidTargetFrameworks);
        Assert.Equal(["net9.0-android", "net9.0-ios", "net9.0-maccatalyst"], project.MauiInfo.TargetFrameworks);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "maui-project");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "maui-android-target");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "maui-application-id");
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Variants));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.MultiDevice));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.Null(project.Limitation);
        Assert.Equal(["Debug", "Release"], project.Variants.Select(variant => variant.Name));
        Assert.Equal(2, project.RunConfigurations.Count);
        Assert.All(project.RunConfigurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.Maui, configuration.Kind);
            Assert.Equal("net9.0-android", configuration.Build.TargetFramework);
            Assert.Equal("MauiSample", configuration.Build.WorkingDirectory);
            Assert.Equal("MauiSample.csproj", configuration.Build.Task);
            Assert.Equal("com.example.mauiapp", configuration.Launch.PackageName);
        });
        var debug = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "Debug");
        Assert.True(debug.IsDefault);
        Assert.Equal(
            "MauiSample/bin/Debug/net9.0-android/**/*-Signed.apk",
            debug.Artifact.Glob);
    }

    [Fact]
    public async Task DetectAsync_DetectsMauiFromTheControlsPackageReference()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "Mobile.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0-android</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Maui.Controls" Version="8.0.100" />
              </ItemGroup>
            </Project>
            """);
        var detector = new MauiProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal(ProjectKind.Maui, result.Project.Kind);
        Assert.True(result.Project.MauiInfo?.ReferencesMauiControls);
        Assert.Equal(["net8.0-android"], result.Project.MauiInfo?.AndroidTargetFrameworks);
        Assert.Equal("Mobile", result.Project.Name);
    }

    [Fact]
    public async Task DetectAsync_MarksProjectsWithoutAnAndroidTargetAsLimited()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "MauiApp.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net9.0-ios;net9.0-maccatalyst</TargetFrameworks>
                <UseMaui>true</UseMaui>
              </PropertyGroup>
            </Project>
            """);
        var detector = new MauiProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Maui, project.Kind);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.False(string.IsNullOrWhiteSpace(project.Limitation));
        Assert.Contains("-android", project.Limitation, StringComparison.Ordinal);
        Assert.Contains("TargetFrameworks", project.Limitation, StringComparison.Ordinal);
        Assert.Empty(project.Variants);
        Assert.Empty(project.RunConfigurations);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullForProjectsThatAreNotMaui()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "ConsoleApp.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        var detector = new MauiProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutAProjectFile()
    {
        using var temp = new TempDirectory();
        var detector = new MauiProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static void WriteMauiProject(TempDirectory temp, string path, string name)
    {
        temp.WriteFile(
            path,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net9.0-android;net9.0-ios;net9.0-maccatalyst</TargetFrameworks>
                <UseMaui>true</UseMaui>
                <ApplicationId>com.example.mauiapp</ApplicationId>
                <ApplicationTitle>Maui Sample</ApplicationTitle>
              </PropertyGroup>
            </Project>
            """);
    }
}
