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
    public async Task DetectAsync_ResolvesCatalogAliasesAndConventionPluginsWithFlavorSuffixes()
    {
        using var temp = new TempDirectory();
        WriteCatalog(temp);
        temp.WriteFile(
            "settings.gradle.kts",
            """
            pluginManagement {
                includeBuild("build-logic-convention")
            }
            rootProject.name = "JellyPlay"
            include(":app")
            include(":shared:core:model")
            """);
        WriteConventionBuild(temp);
        temp.WriteFile(
            "build.gradle.kts",
            """
            plugins {
                alias(libs.plugins.android.application) apply false
            }
            """);
        temp.WriteFile(
            "app/build.gradle.kts",
            """
            plugins {
                alias(libs.plugins.android.application)
            }

            android {
                namespace = "com.example.media"
                defaultConfig {
                    applicationId = "com.example.media"
                }
                flavorDimensions += "platform"
                productFlavors {
                    create("phone") {
                        dimension = "platform"
                    }
                    create("tv") {
                        dimension = "platform"
                        applicationIdSuffix = ".tv"
                    }
                }
                buildTypes {
                    debug {
                        applicationIdSuffix = ".dev"
                    }
                    release {
                        isMinifyEnabled = false
                    }
                }
            }
            """);
        temp.WriteFile(
            "shared/core/model/build.gradle.kts",
            """
            plugins {
                id("jellyplay.kmp.library.compose")
            }

            kotlin {
                android {
                    namespace = "com.example.media.shared.model"
                }
            }
            """);
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.KotlinMultiplatform, project.Kind);
        Assert.True(project.IsKotlinMultiplatform);
        Assert.True(project.IsComposeMultiplatform);
        Assert.Equal("8.13.0", project.AgpVersion);
        Assert.Equal("2.2.20", project.KotlinVersion);
        Assert.Equal(":app", project.ApplicationModule);
        Assert.Equal("com.example.media", project.ApplicationId);
        Assert.Equal(
            [":app", ":shared:core:model"],
            project.Modules.Select(module => module.Name));
        var shared = project.Modules.Single(module => module.Name == ":shared:core:model");
        Assert.True(shared.IsKotlinMultiplatform);
        Assert.True(shared.HasAndroidTarget);
        Assert.True(shared.IsComposeMultiplatform);
        Assert.Equal(ProjectModuleType.Library, shared.Type);
        Assert.Equal(
            ["phoneDebug", "phoneRelease", "tvDebug", "tvRelease"],
            project.Variants.Select(variant => variant.Name));
        Assert.Equal(
            ["com.example.media.dev", "com.example.media", "com.example.media.tv.dev", "com.example.media.tv"],
            project.Variants.Select(variant => variant.ApplicationId));
        var defaultVariant = Assert.Single(project.Variants, variant => variant.IsDefault);
        Assert.Equal("phoneDebug", defaultVariant.Name);
        Assert.All(
            project.Variants,
            variant => Assert.Equal($":app:assemble{Capitalize(variant.Name)}", variant.BuildTask));
        Assert.All(
            project.Variants,
            variant => Assert.StartsWith("app/build/outputs/apk/", variant.ArtifactGlob));
        var tvDebug = Assert.Single(project.RunConfigurations, configuration => configuration.VariantName == "tvDebug");
        Assert.Equal(":app:assembleTvDebug", tvDebug.Build.Task);
        Assert.Equal("com.example.media.tv.dev", tvDebug.Artifact.PackageName);
        Assert.Equal("com.example.media.tv.dev", tvDebug.Launch.PackageName);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "kmp-:shared:core:model");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "compose-multiplatform");
    }

    [Fact]
    public async Task DetectAsync_DetectsComposeMultiplatformAppFromCatalogAliases()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            "gradle/libs.versions.toml",
            """
            [versions]
            agp = "8.9.2"
            kotlin = "2.1.0"
            composeMultiplatform = "1.7.3"

            [plugins]
            androidApplication = { id = "com.android.application", version.ref = "agp" }
            kotlinMultiplatform = { id = "org.jetbrains.kotlin.multiplatform", version.ref = "kotlin" }
            composeMultiplatform = { id = "org.jetbrains.compose", version.ref = "composeMultiplatform" }
            composeCompiler = { id = "org.jetbrains.kotlin.plugin.compose", version.ref = "kotlin" }
            """);
        temp.WriteFile("settings.gradle.kts", "include(\":composeApp\")");
        temp.WriteFile(
            "composeApp/build.gradle.kts",
            """
            plugins {
                alias(libs.plugins.kotlinMultiplatform)
                alias(libs.plugins.androidApplication)
                alias(libs.plugins.composeMultiplatform)
                alias(libs.plugins.composeCompiler)
            }

            kotlin {
                androidTarget {
                    compilerOptions {
                        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11)
                    }
                }
            }

            android {
                namespace = "com.example.composeapp"
                defaultConfig {
                    applicationId = "com.example.composeapp"
                }
            }
            """);
        temp.CreateDirectory("composeApp", "src", "commonMain");
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.KotlinMultiplatform, project.Kind);
        Assert.True(project.IsComposeMultiplatform);
        Assert.Equal("com.example.composeapp", project.ApplicationId);
        Assert.Equal("8.9.2", project.AgpVersion);
        Assert.Equal("2.1.0", project.KotlinVersion);
        var module = Assert.Single(project.Modules);
        Assert.Equal(":composeApp", module.Name);
        Assert.True(module.IsKotlinMultiplatform);
        Assert.True(module.HasAndroidTarget);
        Assert.True(module.IsComposeMultiplatform);
        Assert.Equal(ProjectModuleType.Application, module.Type);
        Assert.Equal(["debug", "release"], project.Variants.Select(variant => variant.Name));
        Assert.Equal(":composeApp:assembleDebug", project.Variants[0].BuildTask);
        Assert.Equal("composeApp/build/outputs/apk/debug/*.apk", project.Variants[0].ArtifactGlob);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.MultiDevice));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Launch));
    }

    [Fact]
    public async Task DetectAsync_FallsBackToNamespaceWhenApplicationIdIsNotALiteral()
    {
        using var temp = new TempDirectory();
        WriteCatalog(temp);
        temp.WriteFile("settings.gradle.kts", "rootProject.name = \"tv\"");
        temp.WriteFile(
            "build.gradle.kts",
            """
            plugins {
                alias(libs.plugins.android.application)
            }

            android {
                namespace = "org.example.tv"
                defaultConfig {
                    applicationId = namespace
                }
                buildTypes {
                    debug {
                        applicationIdSuffix = ".debug"
                    }
                }
            }
            """);
        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.AndroidGradle, project.Kind);
        Assert.Equal("org.example.tv", project.ApplicationId);
        Assert.Equal("org.example.tv", project.Namespace);
        var module = Assert.Single(project.Modules);
        Assert.Equal(":", module.Name);
        Assert.Equal(ProjectModuleType.Application, module.Type);
        Assert.Null(module.ApplicationId);
        Assert.Equal(["debug", "release"], project.Variants.Select(variant => variant.Name));
        Assert.Equal("assembleDebug", project.Variants[0].BuildTask);
        Assert.Equal("build/outputs/apk/debug/*.apk", project.Variants[0].ArtifactGlob);
        Assert.Equal("org.example.tv.debug", project.Variants[0].ApplicationId);
        Assert.Equal(
            "org.example.tv.debug",
            Assert.Single(project.RunConfigurations, configuration => configuration.IsDefault).Launch.PackageName);
    }

    [Fact]
    public async Task DetectAsync_QualifiesVariantsAndConfiguredIdsForMultipleAppModules()
    {
        using var temp = new TempDirectory();
        WriteCatalog(temp);
        temp.WriteFile(
            "settings.gradle.kts",
            """
            rootProject.name = "findroid"
            include(":app:phone")
            include(":app:tv")
            """);
        foreach (var module in new[] { "phone", "tv" })
        {
            temp.WriteFile(
                $"app/{module}/build.gradle.kts",
                """
                plugins {
                    alias(libs.plugins.android.application)
                }

                android {
                    namespace = "dev.example.player"
                    defaultConfig {
                        applicationId = "dev.example.player"
                    }
                    buildTypes {
                        named("debug") { applicationIdSuffix = ".debug" }
                        named("release") {
                            isMinifyEnabled = true
                        }
                        register("staging") {
                            initWith(getByName("release"))
                            applicationIdSuffix = ".staging"
                        }
                    }
                    flavorDimensions += "variant"
                    productFlavors {
                        register("libre") {
                            dimension = "variant"
                            isDefault = true
                        }
                    }
                }
                """);
        }

        var detector = new GradleProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(2, project.Modules.Count(module => module.Type == ProjectModuleType.Application));
        Assert.Equal(6, project.Variants.Count);
        Assert.Equal(6, project.RunConfigurations.Count);
        Assert.Equal(6, project.RunConfigurations.Select(configuration => configuration.Id).Distinct().Count());
        var phoneDebug = Assert.Single(
            project.Variants,
            variant => variant.ModuleName == ":app:phone" && variant.Name == "libreDebug");
        Assert.Equal(":app:phone:assembleLibreDebug", phoneDebug.BuildTask);
        Assert.Equal("app/phone/build/outputs/apk/libre/debug/*.apk", phoneDebug.ArtifactGlob);
        Assert.Equal("dev.example.player.debug", phoneDebug.ApplicationId);
        Assert.Single(project.Variants, variant => variant.IsDefault);
        Assert.Contains(
            project.RunConfigurations,
            configuration => configuration.Name == "app/phone libreDebug");
        var staging = Assert.Single(
            project.RunConfigurations,
            configuration => configuration.VariantName == "libreStaging" && configuration.ModuleName == ":app:tv");
        Assert.Equal(":app:tv:assembleLibreStaging", staging.Build.Task);
        Assert.Equal("dev.example.player.staging", staging.Artifact.PackageName);
        Assert.Equal("dev.example.player.staging", staging.Launch.PackageName);
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

    private static void WriteCatalog(TempDirectory temp) =>
        temp.WriteFile(
            "gradle/libs.versions.toml",
            """
            [versions]
            agp = "8.13.0"
            kotlin = "2.2.20"
            composeMultiplatform = "1.9.0"

            [plugins]
            android-application = { id = "com.android.application", version.ref = "agp" }
            kotlin-multiplatform = { id = "org.jetbrains.kotlin.multiplatform", version.ref = "kotlin" }
            android-kotlin-multiplatform-library = { id = "com.android.kotlin.multiplatform.library", version.ref = "agp" }
            compose-multiplatform = { id = "org.jetbrains.compose", version.ref = "composeMultiplatform" }
            """);

    private static void WriteConventionBuild(TempDirectory temp)
    {
        temp.WriteFile(
            "build-logic-convention/build.gradle.kts",
            """
            plugins {
                `kotlin-dsl`
            }

            gradlePlugin {
                plugins {
                    register("kmpLibraryBase") {
                        id = "jellyplay.kmp.library.base"
                        implementationClass = "jellyplay.buildlogic.KmpLibraryBasePlugin"
                    }
                    register("kmpLibraryCompose") {
                        id = "jellyplay.kmp.library.compose"
                        implementationClass = "jellyplay.buildlogic.KmpLibraryComposePlugin"
                    }
                }
            }
            """);
        temp.WriteFile(
            "build-logic-convention/src/main/kotlin/jellyplay/buildlogic/KmpLibraryBasePlugin.kt",
            """
            class KmpLibraryBasePlugin : Plugin<Project> {
                override fun apply(project: Project) {
                    project.pluginManager.apply("org.jetbrains.kotlin.multiplatform")
                    project.pluginManager.apply("com.android.kotlin.multiplatform.library")
                }
            }
            """);
        temp.WriteFile(
            "build-logic-convention/src/main/kotlin/jellyplay/buildlogic/KmpLibraryComposePlugin.kt",
            """
            class KmpLibraryComposePlugin : Plugin<Project> {
                override fun apply(project: Project) {
                    project.pluginManager.apply(KmpLibraryBasePlugin::class.java)
                    project.pluginManager.apply("org.jetbrains.compose")
                }
            }
            """);
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

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
