using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Tests;

public sealed class ProjectRefreshFieldsTests
{
    [Fact]
    public void Compare_EmitsTheStableFieldNamesInTableOrder()
    {
        var changes = ProjectRefreshFields.Compare(new ProjectModel(), CreateDetectedProject());

        Assert.Equal(
            [
                "kind",
                "applicationId",
                "namespace",
                "launcherActivity",
                "applicationLabel",
                "applicationModule",
                "wrapperVersion",
                "agpVersion",
                "kotlinVersion",
                "isKotlinMultiplatform",
                "isComposeMultiplatform",
                "modules",
                "variants",
                "apkFiles",
            ],
            changes.Select(change => change.Field));
        Assert.Equal(ProjectRefreshFields.All.Count, changes.Count);
    }

    [Fact]
    public void Compare_NoChangeProjectionYieldsNoChanges()
    {
        var stored = CreateDetectedProject();

        Assert.Empty(ProjectRefreshFields.Compare(stored, stored.Clone()));
    }

    [Fact]
    public void Compare_ClassifiesAddedRemovedAndUpdatedValues()
    {
        var stored = new ProjectModel
        {
            ApplicationId = "com.example.removed",
            Namespace = "com.example.updated",
        };
        var detected = new ProjectModel
        {
            ApplicationId = null,
            Namespace = "com.example.changed",
            LauncherActivity = ".MainActivity",
        };

        var changes = ProjectRefreshFields.Compare(stored, detected);

        Assert.Equal("removed", Assert.Single(changes, change => change.Field == "applicationId").Kind);
        Assert.Equal("updated", Assert.Single(changes, change => change.Field == "namespace").Kind);
        Assert.Equal("added", Assert.Single(changes, change => change.Field == "launcherActivity").Kind);
    }

    [Fact]
    public void Apply_EveryComparedFieldRoundTrips()
    {
        var stored = new ProjectModel();
        var detected = CreateDetectedProject();
        var changes = ProjectRefreshFields.Compare(stored, detected);
        Assert.Equal(ProjectRefreshFields.All.Count, changes.Count);

        // The service accepts names case-insensitively; use a different casing to prove the lookup.
        ProjectRefreshFields.Apply(stored, detected, changes.Select(change => change.Field.ToUpperInvariant()));

        Assert.Empty(ProjectRefreshFields.Compare(stored, detected));
        Assert.Equal(detected.Kind, stored.Kind);
        Assert.Equal(detected.KindLabel, stored.KindLabel);
        Assert.Same(detected.Modules, stored.Modules);
        Assert.Same(detected.Variants, stored.Variants);
        Assert.Same(detected.ApkFiles, stored.ApkFiles);

        foreach (var change in changes)
        {
            var target = new ProjectModel();
            ProjectRefreshFields.Apply(target, detected, [change.Field]);
            Assert.DoesNotContain(
                ProjectRefreshFields.Compare(target, detected),
                remaining => remaining.Field == change.Field);
        }
    }

    [Fact]
    public void Apply_UpdatesKindLabelWithKind()
    {
        var target = new ProjectModel { Kind = ProjectKind.Custom, KindLabel = "Custom" };
        var detected = new ProjectModel { Kind = ProjectKind.Flutter, KindLabel = "Flutter" };

        ProjectRefreshFields.Apply(target, detected, ["kind"]);

        Assert.Equal(ProjectKind.Flutter, target.Kind);
        Assert.Equal("Flutter", target.KindLabel);
    }

    [Fact]
    public void Apply_IgnoresUnknownFields()
    {
        var target = new ProjectModel { ApplicationId = "com.example.stored" };
        var detected = new ProjectModel { ApplicationId = "com.example.detected" };

        ProjectRefreshFields.Apply(target, detected, ["notAField"]);

        Assert.Equal("com.example.stored", target.ApplicationId);
    }

    private static ProjectModel CreateDetectedProject() => new()
    {
        Kind = ProjectKind.Flutter,
        KindLabel = "Flutter",
        ApplicationId = "com.example.detected",
        Namespace = "com.example.detected",
        LauncherActivity = ".MainActivity",
        ApplicationLabel = "Detected",
        ApplicationModule = "android/app",
        WrapperVersion = "8.9",
        AgpVersion = "8.7.0",
        KotlinVersion = "2.0.21",
        IsKotlinMultiplatform = true,
        IsComposeMultiplatform = true,
        Modules =
        [
            new ProjectModule
            {
                Name = "android/app",
                Path = "android/app",
                Type = ProjectModuleType.Application,
            },
        ],
        Variants =
        [
            new ProjectVariant { Name = "debug", BuildType = "debug" },
        ],
        ApkFiles =
        [
            new ApkFileReference { Path = "android/app/build/outputs/apk/debug/app-debug.apk" },
        ],
    };
}
