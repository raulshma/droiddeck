using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Tests.Projects;

public sealed class RunConfigurationValidatorTests
{
    [Fact]
    public void Validate_AcceptsACompleteGradleConfiguration()
    {
        var project = Project();
        var configuration = new RunConfiguration
        {
            Name = "Debug",
            Kind = RunConfigurationKind.Gradle,
            Build = new BuildConfiguration { Task = "assembleDebug" },
            Artifact = new ArtifactConfiguration { Glob = "app/build/outputs/apk/debug/*.apk" },
        };

        var result = RunConfigurationValidator.Validate(project, configuration);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_RequiresNameTaskAndGlobForGradle()
    {
        var configuration = new RunConfiguration
        {
            Name = " ",
            Kind = RunConfigurationKind.Gradle,
            Build = new BuildConfiguration { Task = string.Empty },
            Artifact = new ArtifactConfiguration { Glob = string.Empty },
        };

        var result = RunConfigurationValidator.Validate(Project(), configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Field == "name");
        Assert.Contains(result.Errors, error => error.Field == "build.task");
        Assert.Contains(result.Errors, error => error.Field == "artifact.glob");
    }

    [Fact]
    public void Validate_RequiresAnApkForApkConfigurations()
    {
        var configuration = new RunConfiguration
        {
            Name = "APK",
            Kind = RunConfigurationKind.Apk,
            Artifact = new ArtifactConfiguration { Glob = string.Empty },
        };

        var result = RunConfigurationValidator.Validate(Project(), configuration);

        Assert.Contains(result.Errors, error => error.Field == "artifact.glob");
    }

    [Fact]
    public void Validate_AcceptsApkConfigurationWhenTheProjectHasApks()
    {
        var project = Project();
        project.ApkFiles.Add(new ApkFileReference { Path = @"C:\repo\app.apk" });
        var configuration = new RunConfiguration
        {
            Name = "APK",
            Kind = RunConfigurationKind.Apk,
            Artifact = new ArtifactConfiguration { Glob = string.Empty },
        };

        var result = RunConfigurationValidator.Validate(project, configuration);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RequiresAnEnabledCustomStepWithACommand()
    {
        var configuration = new RunConfiguration
        {
            Name = "Custom",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition { Id = "one", Name = "Disabled", Enabled = false, Command = "tool" },
                new RunStepDefinition { Id = "two", Name = "Empty", Command = " " },
            ],
        };

        var result = RunConfigurationValidator.Validate(Project(), configuration);

        Assert.Contains(result.Errors, error => error.Field == "steps");
    }

    [Fact]
    public void Validate_ChecksTimeoutsAndLogBounds()
    {
        var configuration = new RunConfiguration
        {
            Name = "Debug",
            Kind = RunConfigurationKind.Gradle,
            Build = new BuildConfiguration { Task = "assembleDebug", TimeoutSeconds = 1 },
            Artifact = new ArtifactConfiguration { Glob = "**/*.apk" },
            Logs = new LogConfiguration { MaxLines = 10 },
            Steps = [new RunStepDefinition { Id = "step", Name = "Step", Command = "tool", TimeoutSeconds = 0 }],
        };

        var result = RunConfigurationValidator.Validate(Project(), configuration);

        Assert.Contains(result.Errors, error => error.Field == "build.timeoutSeconds");
        Assert.Contains(result.Errors, error => error.Field == "logs.maxLines");
        Assert.Contains(result.Errors, error => error.Field == "steps.step.timeoutSeconds");
    }

    private static ProjectModel Project() => new()
    {
        Id = "project",
        Name = "Sample",
        RootPath = @"C:\repo\sample",
        Kind = ProjectKind.AndroidGradle,
    };
}
