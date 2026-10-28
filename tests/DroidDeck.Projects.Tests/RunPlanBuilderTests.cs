using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;
using DroidDeck.Projects;
using DroidDeck.Projects.Adapters;

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

    [Fact]
    public void BuildPreview_FlutterConfigurationShowsFlutterBuildAndLaunch()
    {
        var project = FlutterProject();
        var configuration = project.RunConfigurations[0];

        var preview = _builder.BuildPreview(project, configuration, "emulator-5554");

        var build = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Build);
        Assert.True(build.WillRun);
        Assert.Contains("build apk --debug", build.CommandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("gradle", build.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("build/app/outputs/flutter-apk/app-debug.apk", preview.ArtifactGlob);
        var launch = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Launch && step.Name == "Launch");
        Assert.Contains("-n com.example.flutter_sample/com.example.flutter_sample.MainActivity", launch.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBuildRequest_FlutterUsesModeArgumentsAndToolchainPath()
    {
        var flutter = @"C:\tools\flutter\bin\flutter.bat";
        var builder = new RunPlanBuilder(toolchain: new FakeToolchain(flutter));
        var project = FlutterProject();

        var request = builder.CreateBuildRequest(project, project.RunConfigurations[1]);

        Assert.NotNull(request);
        Assert.Equal(flutter, request.FileName);
        Assert.Equal(["build", "apk", "--profile"], request.Arguments);
        Assert.Equal(project.RootPath, request.WorkingDirectory);
    }

    [Fact]
    public void CreateBuildRequest_FlutterHonorsJavaHomeWorkingDirectoryAndExtraArguments()
    {
        var builder = new RunPlanBuilder(toolchain: new FakeToolchain(@"C:\flutter\flutter.bat"));
        var project = FlutterProject();
        var configuration = project.RunConfigurations[0];
        configuration.Build.JavaHome = @"C:\jdk-21";
        configuration.Build.WorkingDirectory = "android";
        configuration.Build.Arguments = ["--dart-define=API=test"];

        var request = builder.CreateBuildRequest(project, configuration);

        Assert.NotNull(request);
        Assert.Equal(Path.Combine(project.RootPath, "android"), request.WorkingDirectory);
        Assert.Equal(@"C:\jdk-21", request.Environment["JAVA_HOME"]);
        Assert.Equal(["build", "apk", "--debug", "--dart-define=API=test"], request.Arguments);
    }

    [Fact]
    public void CreateFlutterRunRequest_UsesMachineModeForTheDevice()
    {
        var builder = new RunPlanBuilder(toolchain: new FakeToolchain(@"C:\flutter\flutter.bat"));
        var project = FlutterProject();
        var configuration = project.RunConfigurations[0];

        var request = builder.CreateFlutterRunRequest(project, configuration, "emulator-5554");

        Assert.NotNull(request);
        Assert.Equal(@"C:\flutter\flutter.bat", request.FileName);
        Assert.Equal(
            ["run", "--machine", "-d", "emulator-5554", "--debug"],
            request.Arguments);
        Assert.Null(request.Timeout);
    }

    private static ProjectModel FlutterProject()
    {
        var project = new ProjectModel
        {
            Id = "flutter-1",
            Name = "flutter_sample",
            RootPath = Path.Combine(Path.GetTempPath(), "droiddeck-flutter-sample"),
            Kind = ProjectKind.Flutter,
            ApplicationId = "com.example.flutter_sample",
            LauncherActivity = ".MainActivity",
        };
        project.RunConfigurations = FlutterAdapter.CreateDefaultRunConfigurations(project);
        return project;
    }

    private sealed class FakeToolchain : IToolchainService
    {
        public FakeToolchain(string flutterPath) =>
            CachedSnapshot = new ToolchainSnapshot(
                [new ToolInfo { Kind = ToolKind.Flutter, DisplayName = "Flutter", IsAvailable = true, Path = flutterPath }],
                DateTimeOffset.UtcNow);

        public ToolchainSnapshot? CachedSnapshot { get; }

        public event EventHandler<ToolchainSnapshot>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public Task<ToolchainSnapshot?> LoadCachedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedSnapshot);

        public Task<ToolchainSnapshot> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedSnapshot!);
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
