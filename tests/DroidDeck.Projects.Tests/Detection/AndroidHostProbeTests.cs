using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class AndroidHostProbeTests
{
    [Fact]
    public async Task ProbeAsync_ReportsMissingHostWithoutAnAndroidFolder()
    {
        using var temp = new TempDirectory();

        var facts = await AndroidHostProbe.ProbeAsync(temp.Path, "android");

        Assert.False(facts.HasAndroidHost);
        Assert.Equal("android", facts.AndroidFolder);
        Assert.Null(facts.AndroidDirectory);
        Assert.Null(facts.BuildFile);
        Assert.Null(facts.BuildFileRelativePath);
        Assert.Null(facts.BuildFileModulePath);
        Assert.Null(facts.ApplicationId);
        Assert.Null(facts.Namespace);
        Assert.Null(facts.GradleApplicationId);
        Assert.Null(facts.LauncherActivity);
        Assert.Null(facts.ApplicationLabel);
    }

    [Fact]
    public async Task ProbeAsync_ReportsTheHostWithoutABuildFile()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("android");

        var facts = await AndroidHostProbe.ProbeAsync(temp.Path, "android");

        Assert.True(facts.HasAndroidHost);
        Assert.Equal(temp.Combine("android"), facts.AndroidDirectory);
        Assert.Null(facts.BuildFile);
        Assert.Null(facts.BuildFileRelativePath);
        Assert.Null(facts.BuildFileModulePath);
        Assert.Null(facts.ApplicationId);
        Assert.Null(facts.Namespace);
        Assert.Equal("android/app", facts.BuildFileEvidenceSource("android/app"));
        Assert.Equal("android/app/src/main/AndroidManifest.xml", facts.ManifestSource);
    }

    [Fact]
    public async Task ProbeAsync_ReadsApplicationIdAndNamespaceFromTheGradleBuildFile()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "android/app/build.gradle.kts",
            """
            android {
                namespace = "com.example.namespace"
                defaultConfig {
                    applicationId = "com.example.application"
                }
            }
            """);

        var facts = await AndroidHostProbe.ProbeAsync(temp.Path, "android");

        Assert.True(facts.HasAndroidHost);
        Assert.Equal("com.example.application", facts.ApplicationId);
        Assert.Equal("com.example.namespace", facts.Namespace);
        Assert.Equal("com.example.application", facts.GradleApplicationId);
        Assert.Equal(temp.Combine("android", "app", "build.gradle.kts"), facts.BuildFile);
        Assert.Equal(Path.Combine("android", "app", "build.gradle.kts"), facts.BuildFileRelativePath);
        Assert.Equal("android/app/build.gradle.kts", facts.BuildFileModulePath);
        Assert.Equal(
            Path.Combine("android", "app", "build.gradle.kts"),
            facts.BuildFileEvidenceSource("unused"));
    }

    [Fact]
    public async Task ProbeAsync_FallsBackToTheNamespaceForTheGradleApplicationId()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "android/app/build.gradle",
            """
            android {
                namespace "com.example.namespace"
            }
            """);

        var facts = await AndroidHostProbe.ProbeAsync(temp.Path, "android");

        Assert.Null(facts.ApplicationId);
        Assert.Equal("com.example.namespace", facts.Namespace);
        Assert.Equal("com.example.namespace", facts.GradleApplicationId);
    }

    [Fact]
    public async Task ProbeAsync_ReadsManifestFactsWhenTheManifestIsTheOnlyHostFile()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("android/app/src/main/AndroidManifest.xml", Manifest(".ManifestOnlyActivity", "Manifest Only"));

        var facts = await AndroidHostProbe.ProbeAsync(temp.Path, "android");

        Assert.True(facts.HasAndroidHost);
        Assert.Null(facts.BuildFile);
        Assert.Null(facts.GradleApplicationId);
        Assert.Equal(".ManifestOnlyActivity", facts.LauncherActivity);
        Assert.Equal("Manifest Only", facts.ApplicationLabel);
        Assert.Equal("android/app/src/main/AndroidManifest.xml", facts.ManifestSource);
    }

    [Fact]
    public async Task ProbeAsync_UsesTheSuppliedHostFolder()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "platforms/android/app/build.gradle",
            """
            android {
                defaultConfig {
                    applicationId "com.example.cordova"
                }
            }
            """);
        temp.WriteFile("platforms/android/app/src/main/AndroidManifest.xml", Manifest(".CordovaActivity", "Cordova"));

        var facts = await AndroidHostProbe.ProbeAsync(temp.Path, "platforms/android");

        Assert.True(facts.HasAndroidHost);
        Assert.Equal("com.example.cordova", facts.GradleApplicationId);
        Assert.Equal("platforms/android/app/build.gradle", facts.BuildFileModulePath);
        Assert.Equal("platforms/android/app/src/main/AndroidManifest.xml", facts.ManifestSource);
    }

    [Fact]
    public async Task FlutterDetector_AdaptsTheSharedFactsWithoutChangingEvidenceOrder()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "pubspec.yaml",
            """
            name: flutter_sample
            environment:
              sdk: '>=3.5.0 <4.0.0'
            dependencies:
              flutter:
                sdk: flutter
            """);
        temp.WriteFile(".metadata", "project_type: app");
        temp.WriteFile(
            "android/app/build.gradle.kts",
            """
            android {
                namespace = "com.example.flutter_sample"
            }
            """);
        temp.WriteFile("android/app/src/main/AndroidManifest.xml", Manifest(".MainActivity", "Flutter Sample"));

        var result = await new FlutterProjectDetector().DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal("com.example.flutter_sample", project.ApplicationId);
        Assert.Equal("com.example.flutter_sample", project.Namespace);
        Assert.Equal(
            [
                "flutter-pubspec",
                "flutter-android-folder",
                "flutter-metadata",
                "flutter-application-id",
                "flutter-launcher-activity",
            ],
            project.Evidence.Select(evidence => evidence.Id));
        var applicationId = Assert.Single(project.Evidence, evidence => evidence.Id == "flutter-application-id");
        Assert.Equal(Path.Combine("android", "app", "build.gradle.kts"), applicationId.Source);
        Assert.Equal("com.example.flutter_sample", applicationId.Snippet);
        Assert.Equal("android/app/build.gradle.kts", Assert.Single(project.Modules).BuildFile);
    }

    [Fact]
    public async Task ReactNativeDetector_KeepsManagedExpoDetectableWithoutAnAndroidHost()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "expo_sample",
              "dependencies": { "expo": "51.0.0", "react-native": "0.74.0" }
            }
            """);
        temp.WriteFile("app.json", """{ "expo": { "name": "expo_sample" } }""");

        var result = await new ReactNativeProjectDetector().DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.ReactNative, project.Kind);
        Assert.Equal("expo-managed", project.ReactNativeInfo?.Workflow);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.Null(project.ApplicationId);
        Assert.Equal(
            ["react-native-package", "react-native-expo-config"],
            project.Evidence.Select(evidence => evidence.Id));
    }

    [Fact]
    public async Task ReactNativeDetector_AdaptsTheSharedFactsWithoutChangingEvidenceOrder()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "rn_sample",
              "scripts": { "android": "react-native run-android" },
              "dependencies": { "react": "18.2.0", "react-native": "0.73.0" }
            }
            """);
        temp.WriteFile("metro.config.js", "module.exports = {};");
        temp.WriteFile(
            "android/app/build.gradle.kts",
            """
            android {
                namespace = "com.example.rn_sample"
                defaultConfig { applicationId = "com.example.rn_sample" }
            }
            """);
        temp.WriteFile("android/app/src/main/AndroidManifest.xml", Manifest(".MainActivity", "RN Sample"));

        var result = await new ReactNativeProjectDetector().DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal(
            [
                "react-native-package",
                "react-native-android-folder",
                "react-native-metro-config",
                "react-native-android-script",
                "react-native-application-id",
                "react-native-launcher-activity",
            ],
            result.Project.Evidence.Select(evidence => evidence.Id));
        Assert.Equal("android/app/src/main/AndroidManifest.xml", Assert.Single(
            result.Project.Evidence,
            evidence => evidence.Id == "react-native-launcher-activity").Source);
    }

    [Fact]
    public async Task CapacitorDetector_KeepsTheConfigApplicationIdPrecedence()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "package.json",
            """
            {
              "name": "capacitor_sample",
              "dependencies": { "@capacitor/core": "^6.0.0", "@capacitor/android": "^6.0.0" }
            }
            """);
        temp.WriteFile(
            "capacitor.config.ts",
            """
            const config = { appId: 'com.example.config', appName: 'Config Name' };
            export default config;
            """);
        temp.WriteFile(
            "android/app/build.gradle",
            """
            android {
                defaultConfig { applicationId "com.example.gradle" }
            }
            """);
        temp.WriteFile("android/app/src/main/AndroidManifest.xml", Manifest(".CapacitorActivity", "Gradle Label"));

        var result = await new CapacitorProjectDetector().DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal("com.example.config", project.ApplicationId);
        Assert.Equal(
            [
                "capacitor-package",
                "capacitor-config",
                "capacitor-android-folder",
                "capacitor-application-id",
                "capacitor-launcher-activity",
            ],
            project.Evidence.Select(evidence => evidence.Id));
        var applicationId = Assert.Single(project.Evidence, evidence => evidence.Id == "capacitor-application-id");
        Assert.Equal("capacitor.config.ts", applicationId.Source);
        Assert.Equal("The project declares application ID com.example.config.", applicationId.Detail);
        Assert.Equal("android/app/src/main/AndroidManifest.xml", Assert.Single(
            project.Evidence,
            evidence => evidence.Id == "capacitor-launcher-activity").Source);
    }

    [Fact]
    public async Task CordovaDetector_KeepsTheWidgetApplicationIdPrecedence()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "config.xml",
            """
            <?xml version='1.0' encoding='utf-8'?>
            <widget id="com.example.widget" version="1.0.0" xmlns="http://www.w3.org/ns/widgets">
              <name>Cordova Sample</name>
            </widget>
            """);
        temp.WriteFile(
            "package.json",
            """{ "name": "cordova_sample", "dependencies": { "cordova": "^12.0.0" } }""");
        temp.WriteFile(
            "platforms/android/app/build.gradle",
            """
            android {
                defaultConfig { applicationId "com.example.gradle" }
            }
            """);
        temp.WriteFile("platforms/android/app/src/main/AndroidManifest.xml", Manifest(".CordovaActivity", "Cordova"));

        var result = await new CordovaProjectDetector().DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal("com.example.widget", project.ApplicationId);
        Assert.Equal(
            [
                "cordova-config",
                "cordova-package",
                "cordova-android-folder",
                "cordova-application-id",
                "cordova-launcher-activity",
            ],
            project.Evidence.Select(evidence => evidence.Id));
        Assert.Equal("config.xml", Assert.Single(
            project.Evidence,
            evidence => evidence.Id == "cordova-application-id").Source);
        Assert.Equal("platforms/android/app/src/main/AndroidManifest.xml", Assert.Single(
            project.Evidence,
            evidence => evidence.Id == "cordova-launcher-activity").Source);
        Assert.Equal("platforms/android/app/build.gradle", Assert.Single(project.Modules).BuildFile);
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static string Manifest(string launcherActivity, string label) =>
        $"""
        <manifest xmlns:android="http://schemas.android.com/apk/res/android">
          <application android:label="{label}">
            <activity android:name="{launcherActivity}" android:exported="true">
              <intent-filter>
                <action android:name="android.intent.action.MAIN" />
                <category android:name="android.intent.category.LAUNCHER" />
              </intent-filter>
            </activity>
          </application>
        </manifest>
        """;
}
