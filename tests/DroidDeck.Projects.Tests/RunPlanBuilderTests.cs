using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;
using DroidDeck.Projects;
using DroidDeck.Projects.Adapters;
using DroidDeck.TestSupport;

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

    [Fact]
    public void CreateBuildRequest_ReactNativeRunsTheWrapperInsideTheAndroidHost()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("android/gradlew.bat", "@echo off");
        var project = ReactNativeProject();
        project.RootPath = temp.Path;
        var builder = new RunPlanBuilder(toolchain: new FakeToolchain(@"C:\flutter\flutter.bat"));

        var request = builder.CreateBuildRequest(project, project.RunConfigurations[0]);

        Assert.NotNull(request);
        Assert.Equal("cmd.exe", request.FileName);
        Assert.Contains("gradlew.bat", request.Arguments[3], StringComparison.Ordinal);
        Assert.Contains(request.Arguments, static argument => argument.Contains("assembleDebug", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(project.RootPath, "android"), request.WorkingDirectory);
    }

    [Fact]
    public void CreateMetroStartRequest_PlansReactNativeThroughNpx()
    {
        var builder = new RunPlanBuilder(toolchain: new FakeToolchain(@"C:\flutter\flutter.bat", @"C:\node\npx.cmd"));
        var project = ReactNativeProject();

        var request = builder.CreateMetroStartRequest(project, project.RunConfigurations[0]);

        Assert.NotNull(request);
        Assert.Equal(@"C:\node\npx.cmd", request.FileName);
        Assert.Equal(["react-native", "start", "--port", "8081"], request.Arguments);
        Assert.Equal(project.RootPath, request.WorkingDirectory);
        Assert.Equal("1", request.Environment["CI"]);
    }

    [Fact]
    public void CreateMetroStartRequest_UsesTheExpoDevClientAndConfiguredPort()
    {
        var builder = new RunPlanBuilder();
        var project = ReactNativeProject(expo: true, port: 8082);

        var request = builder.CreateMetroStartRequest(project, project.RunConfigurations[0]);

        Assert.NotNull(request);
        Assert.Equal("npx.cmd", request.FileName);
        Assert.Equal(["expo", "start", "--dev-client", "--port", "8082"], request.Arguments);
    }

    [Fact]
    public void CreateMetroStartRequest_ReturnsNullForNonReactNativeProjects()
    {
        var builder = new RunPlanBuilder();

        Assert.Null(builder.CreateMetroStartRequest(FlutterProject(), new RunConfiguration()));
        Assert.Null(builder.CreateMetroStartRequest(CustomProject(), new RunConfiguration()));
    }

    [Fact]
    public void BuildPreview_ReactNativeConfigurationShowsTheGradleHostBuild()
    {
        var project = ReactNativeProject();
        var configuration = project.RunConfigurations[0];

        var preview = _builder.BuildPreview(project, configuration, "emulator-5554");

        var build = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Build);
        Assert.True(build.WillRun);
        Assert.Contains("assembleDebug", build.CommandLine, StringComparison.Ordinal);
        Assert.Equal("android/app/build/outputs/apk/debug/*.apk", preview.ArtifactGlob);
        Assert.Contains(preview.Steps, step => step.Name == "Install" && step.WillRun);
    }

    [Fact]
    public void CreateBuildRequest_MauiRunsDotNetBuildForTheSelectedFramework()
    {
        var dotnet = @"C:\Program Files\dotnet\dotnet.exe";
        var builder = new RunPlanBuilder(toolchain: new FakeToolchain(@"C:\flutter\flutter.bat", dotnetPath: dotnet));
        var project = MauiProject();
        var configuration = project.RunConfigurations[0];
        configuration.Build.Arguments = ["--no-restore"];

        var request = builder.CreateBuildRequest(project, configuration);

        Assert.NotNull(request);
        Assert.Equal(dotnet, request.FileName);
        Assert.Equal(
            ["build", "MauiSample.csproj", "-c", "Debug", "-f", "net9.0-android", "--no-restore"],
            request.Arguments);
        Assert.Equal(project.RootPath, request.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(1_800), request.Timeout);
    }

    [Fact]
    public void CreateBuildRequest_MauiNestedProjectBuildsInsideItsFolder()
    {
        using var temp = new TempDirectory();
        var project = new ProjectModel
        {
            Id = "maui-nested",
            Name = "MauiApp",
            RootPath = temp.Path,
            Kind = ProjectKind.Maui,
            MauiInfo = new MauiProjectInfo
            {
                ProjectFile = "MauiApp/MauiApp.csproj",
                TargetFrameworks = ["net9.0-android"],
                AndroidTargetFrameworks = ["net9.0-android"],
            },
        };
        project.RunConfigurations = MauiAdapter.CreateDefaultRunConfigurations(project, project.MauiInfo);

        var request = _builder.CreateBuildRequest(project, project.RunConfigurations[0]);

        Assert.NotNull(request);
        Assert.Equal(Path.Combine(project.RootPath, "MauiApp"), request.WorkingDirectory);
        Assert.Equal(["build", "MauiApp.csproj", "-c", "Debug", "-f", "net9.0-android"], request.Arguments);
    }

    [Fact]
    public void CreateBuildRequest_MauiFallsBackToTheProjectFileInsideItsFolder()
    {
        using var temp = new TempDirectory();
        var project = new ProjectModel
        {
            Id = "maui-nested",
            Name = "MauiApp",
            RootPath = temp.Path,
            Kind = ProjectKind.Maui,
            MauiInfo = new MauiProjectInfo
            {
                ProjectFile = "MauiApp/MauiApp.csproj",
                TargetFrameworks = ["net9.0-android"],
                AndroidTargetFrameworks = ["net9.0-android"],
            },
        };
        project.RunConfigurations = MauiAdapter.CreateDefaultRunConfigurations(project, project.MauiInfo);
        var configuration = project.RunConfigurations[0];
        configuration.Build.Task = null;

        var request = _builder.CreateBuildRequest(project, configuration);

        Assert.NotNull(request);
        Assert.Equal(Path.Combine(project.RootPath, "MauiApp"), request.WorkingDirectory);
        Assert.Equal("MauiApp.csproj", request.Arguments[1]);
    }

    [Fact]
    public void CreateBuildRequest_MauiFallsBackToDotNetOnPath()
    {
        var project = MauiProject();

        var request = _builder.CreateBuildRequest(project, project.RunConfigurations[0]);

        Assert.NotNull(request);
        Assert.Equal("dotnet", request.FileName);
    }

    [Fact]
    public void BuildPreview_MauiConfigurationShowsDotNetBuildAndInstall()
    {
        var project = MauiProject();
        var configuration = project.RunConfigurations[1];

        var preview = _builder.BuildPreview(project, configuration, "emulator-5554");

        Assert.True(preview.IsValid, string.Join("; ", preview.Errors.Select(error => error.Message)));
        var build = Assert.Single(preview.Steps, step => step.Kind == RunStepKind.Build);
        Assert.True(build.WillRun);
        Assert.Contains("build MauiSample.csproj -c Release -f net9.0-android", build.CommandLine, StringComparison.Ordinal);
        Assert.Equal("bin/Release/net9.0-android/**/*-Signed.apk", preview.ArtifactGlob);
        Assert.Contains(preview.Steps, step => step.Name == "Install" && step.WillRun);
        var launch = Assert.Single(preview.Steps, step => step.Name == "Launch");
        Assert.Contains("-n com.example.mauiapp/com.example.mauiapp.MainActivity", launch.CommandLine, StringComparison.Ordinal);
    }

    private static ProjectModel MauiProject()
    {
        var project = new ProjectModel
        {
            Id = "maui-1",
            Name = "MauiSample",
            RootPath = Path.Combine(Path.GetTempPath(), "droiddeck-maui-sample"),
            Kind = ProjectKind.Maui,
            ApplicationId = "com.example.mauiapp",
            LauncherActivity = "com.example.mauiapp.MainActivity",
            MauiInfo = new MauiProjectInfo
            {
                ProjectFile = "MauiSample.csproj",
                TargetFrameworks = ["net9.0-android", "net9.0-ios"],
                AndroidTargetFrameworks = ["net9.0-android"],
            },
        };
        project.RunConfigurations = MauiAdapter.CreateDefaultRunConfigurations(project, project.MauiInfo);
        return project;
    }

    private static ProjectModel ReactNativeProject(bool expo = false, int port = ReactNativeAdapter.DefaultMetroPort)
    {
        var project = new ProjectModel
        {
            Id = "rn-1",
            Name = "rn_sample",
            RootPath = Path.Combine(Path.GetTempPath(), "droiddeck-rn-sample"),
            Kind = ProjectKind.ReactNative,
            ApplicationId = "com.example.rn_sample",
            LauncherActivity = ".MainActivity",
            ReactNativeInfo = new ReactNativeProjectInfo
            {
                IsExpo = expo,
                Workflow = expo ? "expo-prebuild" : "bare",
                MetroPort = port,
            },
        };
        project.RunConfigurations = ReactNativeAdapter.CreateDefaultRunConfigurations(project);
        return project;
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
        public FakeToolchain(string flutterPath, string? npxPath = null, string? dotnetPath = null)
        {
            var tools = new List<ToolInfo>
            {
                new() { Kind = ToolKind.Flutter, DisplayName = "Flutter", IsAvailable = true, Path = flutterPath },
            };
            if (npxPath is not null)
            {
                tools.Add(new ToolInfo { Kind = ToolKind.Npx, DisplayName = "npx", IsAvailable = true, Path = npxPath });
            }

            if (dotnetPath is not null)
            {
                tools.Add(new ToolInfo { Kind = ToolKind.DotNet, DisplayName = ".NET SDK", IsAvailable = true, Path = dotnetPath });
            }

            CachedSnapshot = new ToolchainSnapshot(tools, DateTimeOffset.UtcNow);
        }

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
