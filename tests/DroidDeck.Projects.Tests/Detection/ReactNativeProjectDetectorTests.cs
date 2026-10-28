using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class ReactNativeProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsBareReactNativeProjectsWithRunConfigurations()
    {
        using var temp = new TempDirectory();
        WriteBareReactNativeProject(temp);
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.ReactNative, project.Kind);
        Assert.Equal("React Native", project.KindLabel);
        Assert.Equal("rn_sample", project.Name);
        Assert.Equal(DetectionConfidence.High, project.Confidence);
        Assert.Equal("com.example.rn_sample", project.ApplicationId);
        Assert.Equal("com.example.rn_sample", project.Namespace);
        Assert.Equal(".MainActivity", project.LauncherActivity);
        Assert.NotNull(project.ReactNativeInfo);
        Assert.False(project.ReactNativeInfo.IsExpo);
        Assert.Equal("bare", project.ReactNativeInfo.Workflow);
        Assert.Equal(8081, project.ReactNativeInfo.MetroPort);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "react-native-package");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "react-native-android-folder");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "react-native-application-id");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "react-native-android-script");
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.HotReload));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.MultiDevice));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.Equal(["debug", "release"], project.Variants.Select(variant => variant.Name));
        Assert.True(project.Variants[0].IsDefault);
        Assert.Equal(2, project.RunConfigurations.Count);
        Assert.All(project.RunConfigurations, configuration =>
        {
            Assert.Equal(RunConfigurationKind.ReactNative, configuration.Kind);
            Assert.Equal("android", configuration.Build.WorkingDirectory);
        });
        var debug = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "debug");
        Assert.True(debug.IsDefault);
        Assert.Equal("assembleDebug", debug.Build.Task);
        Assert.Equal("android/app/build/outputs/apk/debug/*.apk", debug.Artifact.Glob);
        Assert.Equal("com.example.rn_sample", debug.Launch.PackageName);
        var release = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "release");
        Assert.Equal("assembleRelease", release.Build.Task);
        Assert.Equal("android/app/build/outputs/apk/release/*.apk", release.Artifact.Glob);
    }

    [Fact]
    public async Task DetectAsync_ManagedExpoShowsLimitedBadgeWithExactSteps()
    {
        using var temp = new TempDirectory();
        WriteManagedExpoProject(temp);
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.ReactNative, project.Kind);
        Assert.Equal("Expo (managed)", project.KindLabel);
        Assert.NotNull(project.ReactNativeInfo);
        Assert.True(project.ReactNativeInfo.IsExpo);
        Assert.Equal("expo-managed", project.ReactNativeInfo.Workflow);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.False(string.IsNullOrWhiteSpace(project.Limitation));
        Assert.Contains("npx expo prebuild --platform android", project.Limitation, StringComparison.Ordinal);
        Assert.Empty(project.Variants);
        Assert.Empty(project.RunConfigurations);
    }

    [Fact]
    public async Task DetectAsync_ExpoWithAndroidHostUsesTheFullWorkflow()
    {
        using var temp = new TempDirectory();
        WriteExpoPrebuildProject(temp);
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.ReactNative, project.Kind);
        Assert.NotNull(project.ReactNativeInfo);
        Assert.True(project.ReactNativeInfo.IsExpo);
        Assert.Equal("expo-prebuild", project.ReactNativeInfo.Workflow);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.Null(project.Limitation);
        Assert.Equal(2, project.RunConfigurations.Count);
    }

    [Fact]
    public async Task DetectAsync_ReadsTheMetroPortFromTheMetroConfig()
    {
        using var temp = new TempDirectory();
        WriteBareReactNativeProject(temp);
        temp.WriteFile(
            "metro.config.js",
            """
            module.exports = {
              transformer: { getTransformOptions: async () => ({ transform: {} }) },
              server: {
                port: 8082,
              },
            };
            """);
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal(8082, result.Project.ReactNativeInfo?.MetroPort);
        Assert.Contains(result.Project.Evidence, evidence => evidence.Id == "react-native-metro-config");
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutReactNativeDependencies()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("package.json", """{ "name": "web-app", "dependencies": { "react": "18.2.0" } }""");
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutPackageJson()
    {
        using var temp = new TempDirectory();
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullForInvalidPackageJson()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("package.json", "{ not json");
        temp.CreateDirectory("android");
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    [Fact]
    public async Task DetectAsync_WarnsWhenTheGradleBuildFileIsMissing()
    {
        using var temp = new TempDirectory();
        WriteBareReactNativeProject(temp, includeGradleBuildFile: false);
        var detector = new ReactNativeProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Contains(
            result.Project.Warnings,
            warning => warning.Contains("application ID", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DetectionConfidence.Medium, result.Project.Confidence);
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static void WriteBareReactNativeProject(TempDirectory temp, bool includeGradleBuildFile = true)
    {
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "rn_sample",
              "version": "1.0.0",
              "scripts": { "android": "react-native run-android" },
              "dependencies": {
                "react": "18.2.0",
                "react-native": "0.73.0"
              }
            }
            """);
        temp.WriteFile("metro.config.js", "module.exports = {};");
        temp.CreateDirectory("android");
        if (includeGradleBuildFile)
        {
            temp.WriteFile(
                "android/app/build.gradle.kts",
                """
                plugins { id("com.android.application") }
                android {
                    namespace = "com.example.rn_sample"
                    defaultConfig { applicationId = "com.example.rn_sample" }
                }
                """);
        }

        temp.WriteFile(
            "android/app/src/main/AndroidManifest.xml",
            """
            <manifest xmlns:android="http://schemas.android.com/apk/res/android">
              <application android:label="RN Sample">
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

    private static void WriteManagedExpoProject(TempDirectory temp)
    {
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "expo_sample",
              "dependencies": {
                "expo": "51.0.0",
                "react-native": "0.74.0"
              }
            }
            """);
        temp.WriteFile(
            "app.json",
            """{ "expo": { "name": "expo_sample", "slug": "expo_sample" } }""");
    }

    private static void WriteExpoPrebuildProject(TempDirectory temp)
    {
        WriteManagedExpoProject(temp);
        temp.CreateDirectory("android");
        temp.WriteFile(
            "android/app/build.gradle.kts",
            """
            android {
                namespace = "com.example.expo_sample"
                defaultConfig { applicationId = "com.example.expo_sample" }
            }
            """);
    }
}
