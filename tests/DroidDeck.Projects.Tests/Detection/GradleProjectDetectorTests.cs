using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class GradleProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsAndroidApplicationAndVariants()
    {
        using var temp = new TempDirectory();
        WriteAndroidProject(temp);
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.AndroidGradle, project.Kind);
        Assert.Equal(DetectionConfidence.High, project.Confidence);
        Assert.Equal(":app", project.ApplicationModule);
        Assert.Equal("com.example.app", project.ApplicationId);
        Assert.Equal("com.example.app", project.Namespace);
        Assert.Equal("com.example.app.MainActivity", project.LauncherActivity);
        Assert.Equal("8.9", project.WrapperVersion);
        Assert.Equal("8.7.0", project.AgpVersion);
        Assert.False(project.IsKotlinMultiplatform);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "gradle-settings");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "application-id-:app");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "launcher-activity");
        Assert.Equal(
            ["freeDebug", "freeRelease", "paidDebug", "paidRelease"],
            project.Variants.Select(variant => variant.Name));
        Assert.True(project.Variants.Single(variant => variant.Name == "freeDebug").IsDefault);
        Assert.Equal("app/build/outputs/apk/free/debug/*.apk", project.Variants[0].ArtifactGlob);
        Assert.Equal(4, project.RunConfigurations.Count);
        Assert.All(project.RunConfigurations, configuration => Assert.Equal(RunConfigurationKind.Gradle, configuration.Kind));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Install));
    }

    [Fact]
    public async Task DetectAsync_DetectsKotlinAndComposeMultiplatformAndroidTarget()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("settings.gradle.kts", "include(\":composeApp\")");
        temp.WriteFile(
            "composeApp/build.gradle.kts",
            """
            plugins {
                id("org.jetbrains.kotlin.multiplatform")
                id("org.jetbrains.compose")
                id("com.android.application")
            }

            kotlin {
                androidTarget()
            }

            android {
                namespace = "com.example.kmp"
                defaultConfig {
                    applicationId = "com.example.kmp"
                }
            }
            """);
        temp.WriteFile(
            "composeApp/src/main/AndroidManifest.xml",
            """
            <manifest xmlns:android="http://schemas.android.com/apk/res/android">
              <application android:label="KMP Sample">
                <activity android:name=".MainActivity" android:exported="true">
                  <intent-filter>
                    <action android:name="android.intent.action.MAIN" />
                    <category android:name="android.intent.category.LAUNCHER" />
                  </intent-filter>
                </activity>
              </application>
            </manifest>
            """);
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.KotlinMultiplatform, project.Kind);
        Assert.True(project.IsKotlinMultiplatform);
        Assert.True(project.IsComposeMultiplatform);
        Assert.Equal(":composeApp", project.ApplicationModule);
        var module = Assert.Single(project.Modules);
        Assert.True(module.IsKotlinMultiplatform);
        Assert.True(module.HasAndroidTarget);
        Assert.True(module.IsComposeMultiplatform);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "kmp-:composeApp");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "compose-multiplatform");
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.MultiDevice));
        Assert.NotEmpty(project.RunConfigurations);
    }

    [Fact]
    public async Task DetectAsync_ResolvesNestedModuleDirectoriesInAMonorepo()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "settings.gradle.kts",
            """
            include(":shared")
            project(":shared").projectDir = file("modules/shared")
            """);
        temp.WriteFile(
            "modules/shared/build.gradle.kts",
            """
            plugins {
                id("com.android.library")
            }
            """);
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var module = Assert.Single(result.Project.Modules);
        Assert.Equal(":shared", module.Name);
        Assert.Equal("modules/shared", module.Path);
        Assert.Equal(ProjectModuleType.Library, module.Type);
        Assert.True(result.Project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.NotNull(result.Project.Limitation);
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWhenNoGradleMarkersExist()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("README.md", "not a project");
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.Null(result);
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default)
    {
        RepositoryRoot = temp.Path,
    };

    private static void WriteAndroidProject(TempDirectory temp)
    {
        temp.WriteFile("settings.gradle.kts", "include(\":app\")");
        temp.WriteFile(
            "gradle/wrapper/gradle-wrapper.properties",
            "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.9-bin.zip");
        temp.WriteFile(
            "build.gradle.kts",
            """
            plugins {
                id("com.android.application") version "8.7.0" apply false
            }
            """);
        temp.WriteFile(
            "app/build.gradle.kts",
            """
            plugins {
                id("com.android.application")
            }

            android {
                namespace = "com.example.app"
                defaultConfig {
                    applicationId = "com.example.app"
                }
                buildTypes {
                    release {
                        isMinifyEnabled = false
                    }
                }
                productFlavors {
                    free {
                    }
                    paid {
                    }
                }
            }
            """);
        temp.WriteFile(
            "app/src/main/AndroidManifest.xml",
            """
            <manifest xmlns:android="http://schemas.android.com/apk/res/android">
              <application android:label="Sample">
                <activity android:name="com.example.app.MainActivity" android:exported="true">
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
