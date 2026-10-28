using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class CapacitorProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsCapacitorProjectsWithRunConfigurations()
    {
        using var temp = new TempDirectory();
        WriteCapacitorProject(temp);
        var detector = new CapacitorProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Capacitor, project.Kind);
        Assert.Equal("Ionic Capacitor", project.KindLabel);
        Assert.Equal("Capacitor Sample", project.Name);
        Assert.Equal(DetectionConfidence.High, project.Confidence);
        Assert.Equal("com.example.capacitorapp", project.ApplicationId);
        Assert.Equal(".MainActivity", project.LauncherActivity);
        Assert.NotNull(project.CapacitorInfo);
        Assert.Equal("capacitor.config.ts", project.CapacitorInfo.ConfigFile);
        Assert.True(project.CapacitorInfo.HasAndroidHost);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "capacitor-package");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "capacitor-config");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "capacitor-android-folder");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "capacitor-application-id");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "capacitor-launcher-activity");
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
            Assert.Equal(RunConfigurationKind.Capacitor, configuration.Kind);
            Assert.Equal("android", configuration.Build.WorkingDirectory);
            Assert.Equal("com.example.capacitorapp", configuration.Launch.PackageName);
            Assert.Equal(".MainActivity", configuration.Launch.Activity);
        });
        var debug = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "debug");
        Assert.True(debug.IsDefault);
        Assert.Equal("assembleDebug", debug.Build.Task);
        Assert.Equal("android/app/build/outputs/apk/debug/*.apk", debug.Artifact.Glob);
    }

    [Fact]
    public async Task DetectAsync_MarksProjectsWithoutTheAndroidHostAsLimited()
    {
        using var temp = new TempDirectory();
        WriteCapacitorPackage(temp);
        temp.WriteFile(
            "capacitor.config.json",
            """{ "appId": "com.example.capacitorapp", "appName": "Capacitor Sample", "webDir": "dist" }""");
        var detector = new CapacitorProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Capacitor, project.Kind);
        Assert.NotNull(project.CapacitorInfo);
        Assert.False(project.CapacitorInfo.HasAndroidHost);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.False(string.IsNullOrWhiteSpace(project.Limitation));
        Assert.Contains("npx cap add android", project.Limitation, StringComparison.Ordinal);
        Assert.Empty(project.Variants);
        Assert.Empty(project.RunConfigurations);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutCapacitorDependencies()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "package.json",
            """{ "name": "web-app", "dependencies": { "react": "18.2.0" } }""");
        var detector = new CapacitorProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutPackageJson()
    {
        using var temp = new TempDirectory();
        var detector = new CapacitorProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    [Fact]
    public async Task DetectAsync_ReadsTheApplicationIdFromTheConfigWhenTheHostIsMissing()
    {
        using var temp = new TempDirectory();
        WriteCapacitorPackage(temp);
        temp.WriteFile(
            "capacitor.config.ts",
            """
            const config = {
              appId: 'com.example.fromconfig',
              appName: 'Config Only',
            };
            export default config;
            """);
        var detector = new CapacitorProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal("Config Only", result.Project.Name);
        Assert.Equal("com.example.fromconfig", result.Project.ApplicationId);
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static void WriteCapacitorPackage(TempDirectory temp)
    {
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "capacitor_sample",
              "version": "1.0.0",
              "dependencies": {
                "@capacitor/core": "^6.0.0",
                "@capacitor/android": "^6.0.0",
                "@ionic/react": "^8.0.0"
              }
            }
            """);
    }

    private static void WriteCapacitorProject(TempDirectory temp)
    {
        WriteCapacitorPackage(temp);
        temp.WriteFile(
            "capacitor.config.ts",
            """
            import type { CapacitorConfig } from '@capacitor/cli';

            const config: CapacitorConfig = {
              appId: 'com.example.capacitorapp',
              appName: 'Capacitor Sample',
              webDir: 'dist',
            };

            export default config;
            """);
        temp.WriteFile(
            "android/app/build.gradle",
            """
            android {
                namespace "com.example.capacitorapp"
                defaultConfig {
                    applicationId "com.example.capacitorapp"
                }
            }
            """);
        temp.WriteFile(
            "android/app/src/main/AndroidManifest.xml",
            """
            <manifest xmlns:android="http://schemas.android.com/apk/res/android">
              <application android:label="Capacitor Sample">
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
