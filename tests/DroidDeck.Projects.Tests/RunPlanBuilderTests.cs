using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Projects;

namespace DroidDeck.Projects.Tests;

public sealed class RunPlanBuilderTests
{
    private readonly RunPlanBuilder _builder = new();

    [Fact]
    public void BuildPreview_MonkeyFallbackMatchesExecutionWhenActivityUnknown()
    {
        var preview = _builder.BuildPreview(
            CustomProject(),
            new RunConfiguration
            {
                Name = "run",
                Kind = RunConfigurationKind.Custom,
                Launch = { PackageName = "com.example.app" },
                Steps = [new RunStepDefinition { Id = "noop", Command = "cmd", Arguments = ["/c", "echo", "hi"] }],
            });

        var launch = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Launch);
        Assert.True(launch.WillRun);
        Assert.Contains("monkey", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-p", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("com.example.app", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("android.intent.category.LAUNCHER", launch.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("am start", launch.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreview_AmStartMatchesExecutionWhenActivityKnown()
    {
        var preview = _builder.BuildPreview(
            CustomProject("com.example.app", "com.example.app.MainActivity"),
            new RunConfiguration
            {
                Name = "run",
                Kind = RunConfigurationKind.Custom,
                Launch = { PackageName = "com.example.app" },
                Steps = [new RunStepDefinition { Id = "noop", Command = "cmd", Arguments = ["/c", "echo", "hi"] }],
            });

        var launch = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Launch);
        Assert.Contains("am", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("start", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-W", launch.CommandLine, StringComparison.Ordinal);
        Assert.Contains("-n com.example.app/com.example.app.MainActivity", launch.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreview_IncludesForceStopAndClearLogcatSteps()
    {
        var preview = _builder.BuildPreview(
            CustomProject(),
            new RunConfiguration
            {
                Name = "run",
                Kind = RunConfigurationKind.Custom,
                Launch = { PackageName = "com.example.app", StopBeforeLaunch = true },
                Logs = { Attach = true, ClearBeforeLaunch = true },
                Steps = [new RunStepDefinition { Id = "noop", Command = "cmd", Arguments = ["/c", "echo", "hi"] }],
            });

        var names = preview.Steps.Select(step => step.Name).ToArray();
        var launchIndex = Array.IndexOf(names, "Launch");
        var forceStopIndex = Array.IndexOf(names, "Force stop");
        var clearIndex = Array.IndexOf(names, "Clear logcat");
        Assert.True(forceStopIndex >= 0 && forceStopIndex < launchIndex, "Force stop must precede Launch.");
        Assert.True(clearIndex >= 0 && clearIndex < launchIndex, "Clear logcat must precede Launch.");
        var forceStop = preview.Steps[forceStopIndex];
        Assert.Contains("force-stop com.example.app", forceStop.CommandLine, StringComparison.Ordinal);
        Assert.Contains("logcat -c", preview.Steps[clearIndex].CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPreview_CustomConfigWithoutPackageSkipsLaunchInstallAndLogcat()
    {
        var preview = _builder.BuildPreview(
            CustomProject(),
            new RunConfiguration
            {
                Name = "run",
                Kind = RunConfigurationKind.Custom,
                Steps = [new RunStepDefinition { Id = "noop", Command = "cmd", Arguments = ["/c", "echo", "hi"] }],
            });

        var launch = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Launch);
        Assert.False(launch.WillRun);
        Assert.False(string.IsNullOrWhiteSpace(launch.SkipReason));
        Assert.DoesNotContain(preview.Steps, step => step.Name == "Install");
        var logcat = Assert.Single(preview.Steps, step => step.Name == "Attach logcat");
        Assert.False(logcat.WillRun);
    }

    private static ProjectModel CustomProject(
        string? applicationId = null,
        string? launcherActivity = null) => new()
    {
        Id = "proj-1",
        Name = "Sample",
        RootPath = Path.GetTempPath(),
        Kind = ProjectKind.Custom,
        ApplicationId = applicationId ?? string.Empty,
        LauncherActivity = launcherActivity ?? string.Empty,
    };
}
