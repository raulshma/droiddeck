using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class CordovaProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsCordovaProjectsWithRunConfigurations()
    {
        using var temp = new TempDirectory();
        WriteCordovaProject(temp);
        var detector = new CordovaProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Cordova, project.Kind);
        Assert.Equal("Apache Cordova", project.KindLabel);
        Assert.Equal("Cordova Sample", project.Name);
        Assert.Equal(DetectionConfidence.High, project.Confidence);
        Assert.Equal("com.example.cordovaapp", project.ApplicationId);
        Assert.Equal(".MainActivity", project.LauncherActivity);
        Assert.NotNull(project.CordovaInfo);
        Assert.Equal("config.xml", project.CordovaInfo.ConfigFile);
        Assert.True(project.CordovaInfo.HasAndroidHost);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "cordova-config");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "cordova-package");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "cordova-android-folder");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "cordova-application-id");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "cordova-launcher-activity");
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Variants));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.MultiDevice));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.Null(project.Limitation);
        Assert.Equal(["debug", "release"], project.Variants.Select(variant => variant.Name));
        Assert.Equal(2, project.RunConfigurations.Count);
        Assert.All(project.RunConfigurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.Cordova, configuration.Kind);
            Assert.Equal("platforms/android", configuration.Build.WorkingDirectory);
            Assert.Equal("com.example.cordovaapp", configuration.Launch.PackageName);
            Assert.Equal(".MainActivity", configuration.Launch.Activity);
        });
        var debug = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "debug");
        Assert.True(debug.IsDefault);
        Assert.Equal("assembleDebug", debug.Build.Task);
        Assert.Equal("platforms/android/app/build/outputs/apk/debug/*.apk", debug.Artifact.Glob);
    }

    [Fact]
    public async Task DetectAsync_MarksProjectsWithoutTheAndroidPlatformAsLimited()
    {
        using var temp = new TempDirectory();
        WriteCordovaConfig(temp);
        WriteCordovaPackage(temp);
        var detector = new CordovaProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Cordova, project.Kind);
        Assert.NotNull(project.CordovaInfo);
        Assert.False(project.CordovaInfo.HasAndroidHost);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.False(string.IsNullOrWhiteSpace(project.Limitation));
        Assert.Contains("npx cordova platform add android", project.Limitation, StringComparison.Ordinal);
        Assert.Empty(project.Variants);
        Assert.Empty(project.RunConfigurations);
    }

    [Fact]
    public async Task DetectAsync_DetectsCordovaFromConfigXmlAlone()
    {
        using var temp = new TempDirectory();
        WriteCordovaConfig(temp);
        var detector = new CordovaProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal(ProjectKind.Cordova, result.Project.Kind);
        Assert.Equal("Cordova Sample", result.Project.Name);
        Assert.Equal("com.example.cordovaapp", result.Project.ApplicationId);
        Assert.True(result.Project.Capabilities.HasFlag(ProjectCapability.Limited));
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutAWidgetConfig()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("config.xml", "<configuration><appSettings /></configuration>");
        var detector = new CordovaProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutCordovaFiles()
    {
        using var temp = new TempDirectory();
        var detector = new CordovaProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static void WriteCordovaConfig(TempDirectory temp)
    {
        temp.WriteFile(
            "config.xml",
            """
            <?xml version='1.0' encoding='utf-8'?>
            <widget id="com.example.cordovaapp" version="1.0.0" xmlns="http://www.w3.org/ns/widgets">
              <name>Cordova Sample</name>
              <content src="index.html" />
            </widget>
            """);
    }

    private static void WriteCordovaPackage(TempDirectory temp)
    {
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "cordova_sample",
              "version": "1.0.0",
              "dependencies": {
                "cordova": "^12.0.0",
                "cordova-android": "^13.0.0"
              }
            }
            """);
    }

    private static void WriteCordovaProject(TempDirectory temp)
    {
        WriteCordovaConfig(temp);
        WriteCordovaPackage(temp);
        temp.WriteFile(
            "platforms/android/app/build.gradle",
            """
            android {
                namespace "com.example.cordovaapp"
                defaultConfig {
                    applicationId "com.example.cordovaapp"
                }
            }
            """);
        temp.WriteFile(
            "platforms/android/app/src/main/AndroidManifest.xml",
            """
            <manifest xmlns:android="http://schemas.android.com/apk/res/android">
              <application android:label="Cordova Sample">
                <activity android:name=".MainActivity" android:exported="true">
                  <intent-filter>
                    <action android:name="android.intent.action.MAIN" />
                    <category android:name="android.intent.category.LAUNCHER" />
                  </intent-filter>
                </activity>
              </application>
            </manifest>
            """);
    }
}
