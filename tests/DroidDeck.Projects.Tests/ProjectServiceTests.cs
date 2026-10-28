using DroidDeck.Core;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Projects.Detection;
using DroidDeck.Projects.Storage;
using DroidDeck.Projects.Tests.Fakes;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests;

public sealed class ProjectServiceTests
{
    [Fact]
    public async Task AddApkAsync_PersistsLimitedApkProject()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var apk = temp.WriteFile("app.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(apk, new ApkInfo
        {
            PackageName = "com.example.apk",
            VersionName = "2.0",
            ApplicationLabel = "APK Sample",
            LaunchableActivity = "com.example.apk.MainActivity",
        });
        var store = new JsonProjectStore(paths);
        using var service = CreateService(store, inspector);

        var project = await service.AddApkAsync([apk]);

        Assert.Equal(ProjectKind.ApkOnly, project.Kind);
        Assert.Equal("APK Sample", project.Name);
        Assert.Equal("com.example.apk", project.ApplicationId);
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.NotNull(project.Limitation);
        Assert.Single(project.ApkFiles);
        Assert.NotEmpty(project.RunConfigurations);
        Assert.Equal(Path.GetFullPath(apk), project.RunConfigurations[0].Artifact.Glob);

        using var reloaded = CreateService(store, inspector);
        await reloaded.InitializeAsync();
        var persisted = Assert.Single(reloaded.Projects);
        Assert.Equal(project.Id, persisted.Id);
        Assert.Equal(ProjectKind.ApkOnly, persisted.Kind);
    }

    [Fact]
    public async Task AddApkAsync_ReturnsExistingProjectForTheSamePackage()
    {
        using var temp = new TempDirectory();
        var apk = temp.WriteFile("app.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(apk, new ApkInfo { PackageName = "com.example.apk" });
        using var service = CreateService(new JsonProjectStore(new AppPaths(temp.Path)), inspector);

        var first = await service.AddApkAsync([apk]);
        var second = await service.AddApkAsync([apk]);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(service.Projects);
    }

    [Fact]
    public async Task AddApkAsync_RejectsUninspectableAndNonApkFiles()
    {
        using var temp = new TempDirectory();
        var apk = temp.WriteFile("app.apk", "not an apk");
        var text = temp.WriteFile("app.txt", "text");
        using var service = CreateService(new JsonProjectStore(new AppPaths(temp.Path)), new FakeApkInspector());

        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => service.AddApkAsync([apk]));
        var wrongExtension = await Assert.ThrowsAsync<InvalidDataException>(() => service.AddApkAsync([text]));

        Assert.Contains("could not be inspected", invalid.Message);
        Assert.Contains(".apk", wrongExtension.Message);
    }

    [Fact]
    public async Task AddAsync_DetectsGradleProjectAndRefreshAppliesAcceptedChanges()
    {
        using var temp = new TempDirectory();
        WriteAndroidProject(temp);
        var store = new JsonProjectStore(new AppPaths(temp.Path));
        using var service = CreateService(store, new FakeApkInspector());

        var project = await service.AddAsync(temp.Path);

        Assert.Equal(ProjectKind.AndroidGradle, project.Kind);
        Assert.Equal("com.example.app", project.ApplicationId);
        Assert.Single(service.Projects);

        temp.WriteFile(
            "app/build.gradle.kts",
            """
            plugins {
                id("com.android.application")
            }

            android {
                namespace = "com.example.app"
                defaultConfig {
                    applicationId = "com.example.changed"
                }
            }
            """);
        var diff = await service.PreviewRefreshAsync(project.Id);

        Assert.True(diff.HasChanges);
        var change = Assert.Single(diff.Changes, item => item.Field == "applicationId");
        Assert.Equal("com.example.changed", change.NewValue);

        await service.ApplyRefreshAsync(diff, ["applicationId"]);

        Assert.Equal("com.example.changed", project.ApplicationId);
    }

    private static ProjectService CreateService(IProjectStore store, IApkInspector inspector) =>
        new(store, [new GradleProjectDetector()], inspector);

    private static void WriteAndroidProject(TempDirectory temp)
    {
        temp.WriteFile("settings.gradle.kts", "include(\":app\")");
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
