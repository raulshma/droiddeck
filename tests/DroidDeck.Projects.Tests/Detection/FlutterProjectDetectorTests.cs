using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class FlutterProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsFlutterApplicationAndRunConfigurations()
    {
        using var temp = new TempDirectory();
        WriteFlutterProject(temp);
        var detector = new FlutterProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Flutter, project.Kind);
        Assert.Equal("Flutter", project.KindLabel);
        Assert.Equal("flutter_sample", project.Name);
        Assert.Equal(DetectionConfidence.High, project.Confidence);
        Assert.Equal("com.example.flutter_sample", project.ApplicationId);
        Assert.Equal("com.example.flutter_sample", project.Namespace);
        Assert.Equal(".MainActivity", project.LauncherActivity);
        Assert.Equal(">=3.5.0 <4.0.0", project.FlutterEnvironment);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "flutter-pubspec");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "flutter-android-folder");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "flutter-application-id");
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.HotReload));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Tests));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.MultiDevice));
        Assert.Equal(["debug", "profile", "release"], project.Variants.Select(variant => variant.Name));
        Assert.True(project.Variants[0].IsDefault);
        Assert.Equal(3, project.RunConfigurations.Count);
        Assert.All(project.RunConfigurations, configuration => Assert.Equal(RunConfigurationKind.Flutter, configuration.Kind));
        var debug = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "debug");
        Assert.True(debug.IsDefault);
        Assert.Equal("build/app/outputs/flutter-apk/app-debug.apk", debug.Artifact.Glob);
        Assert.Equal("com.example.flutter_sample", debug.Launch.PackageName);
        Assert.Equal(".MainActivity", debug.Launch.Activity);
        var profile = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "profile");
        Assert.Equal("build/app/outputs/flutter-apk/app-profile.apk", profile.Artifact.Glob);
        var release = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "release");
        Assert.Equal("build/app/outputs/flutter-apk/app-release.apk", release.Artifact.Glob);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutTheAndroidFolder()
    {
        using var temp = new TempDirectory();
        WriteFlutterProject(temp, includeAndroidFolder: false);
        var detector = new FlutterProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullForADartPackageWithoutFlutter()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "pubspec.yaml",
            """
            name: dart_only
            environment:
              sdk: '>=3.5.0 <4.0.0'
            dependencies:
              http: ^1.2.0
            """);
        temp.WriteFile("android/app/build.gradle.kts", "plugins { id(\"com.android.application\") }");
        var detector = new FlutterProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutPubspec()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("android/app/build.gradle.kts", "plugins { id(\"com.android.application\") }");
        var detector = new FlutterProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    private static ProjectScanContext Context(TempDirectory temp) =>
        new(temp.Path, ProjectScanOptions.Default);

    private static void WriteFlutterProject(TempDirectory temp, bool includeAndroidFolder = true)
    {
        temp.WriteFile(
            "pubspec.yaml",
            """
            name: flutter_sample
            description: A Flutter sample.
            publish_to: 'none'
            version: 1.0.0+1

            environment:
              sdk: '>=3.5.0 <4.0.0'

            dependencies:
              flutter:
                sdk: flutter

            flutter:
              uses-material-design: true
            """);
        temp.WriteFile(
            ".metadata",
            """
            version:
              revision: "abcdef"
              channel: "stable"
            project_type: app
            """);
        if (!includeAndroidFolder)
        {
            return;
        }

        temp.WriteFile(
            "android/app/build.gradle.kts",
            """
            plugins {
                id("com.android.application")
                id("kotlin-android")
                id("dev.flutter.flutter-gradle-plugin")
            }

            android {
                namespace = "com.example.flutter_sample"

                defaultConfig {
                    applicationId = "com.example.flutter_sample"
                }
            }
            """);
        temp.WriteFile(
            "android/app/src/main/AndroidManifest.xml",
            """
            <manifest xmlns:android="http://schemas.android.com/apk/res/android">
              <application android:label="Flutter Sample">
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
