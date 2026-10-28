using DroidDeck.Core.Configuration;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects;

/// <summary>Builds the exact commands a run configuration will execute.</summary>
public sealed class RunPlanBuilder : IRunPlanBuilder
{
    private readonly IConfigurationService? _configuration;
    private readonly IToolchainService? _toolchain;

    /// <summary>Creates a plan builder that resolves the adb path from settings and the toolchain.</summary>
    public RunPlanBuilder(IConfigurationService? configuration = null, IToolchainService? toolchain = null)
    {
        _configuration = configuration;
        _toolchain = toolchain;
    }

    /// <inheritdoc />
    public RunPlanPreview BuildPreview(
        ProjectModel project,
        RunConfiguration configuration,
        string? deviceSerial = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        var validation = RunConfigurationValidator.Validate(project, configuration);
        var context = RunTemplateContext.ForProject(project, configuration);
        context.DeviceSerial = string.IsNullOrWhiteSpace(deviceSerial) ? configuration.DeviceSerial : deviceSerial;
        var preview = new RunPlanPreview
        {
            ArtifactGlob = TemplateEngine.Expand(configuration.Artifact.Glob, context),
            Errors = [.. validation.Errors],
        };
        var serial = string.IsNullOrWhiteSpace(context.DeviceSerial) ? "<device>" : context.DeviceSerial;
        var adb = ToolPathLocator.ResolveAdb(_configuration, _toolchain);

        var isBuildable = configuration.Kind
            is RunConfigurationKind.Gradle
            or RunConfigurationKind.Flutter
            or RunConfigurationKind.ReactNative
            or RunConfigurationKind.Maui
            or RunConfigurationKind.Capacitor
            or RunConfigurationKind.Cordova;
        if (isBuildable && !configuration.Build.Skip)
        {
            var commands = CreateBuildCommands(project, configuration);
            if (commands.Count == 0)
            {
                preview.Steps.Add(new RunPlanPreviewStep
                {
                    Name = "Build",
                    Kind = RunStepKind.Build,
                    CommandLine = string.Empty,
                    WillRun = false,
                    SkipReason = "The build is disabled for this configuration.",
                });
            }
            else
            {
                foreach (var command in commands)
                {
                    preview.Steps.Add(new RunPlanPreviewStep
                    {
                        Name = command.Name,
                        Kind = RunStepKind.Build,
                        CommandLine = command.Request.ToCommandLine(),
                        WillRun = true,
                    });
                }
            }
        }
        else if (isBuildable)
        {
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Build",
                Kind = RunStepKind.Build,
                CommandLine = string.Empty,
                WillRun = false,
                SkipReason = "The build is disabled for this configuration.",
            });
        }

        var hasArtifact = !string.IsNullOrWhiteSpace(preview.ArtifactGlob) ||
                          project.ApkFiles.Count > 0 ||
                          configuration.Kind == RunConfigurationKind.Gradle;
        if (hasArtifact)
        {
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Locate artifact",
                Kind = RunStepKind.Install,
                CommandLine = string.IsNullOrWhiteSpace(preview.ArtifactGlob)
                    ? project.ApkFiles.FirstOrDefault()?.Path ?? string.Empty
                    : preview.ArtifactGlob,
                WillRun = true,
            });
        }

        if (hasArtifact && !configuration.Install.ExtraArguments.Contains("--no-install", StringComparer.Ordinal))
        {
            var installCommand = BuildInstallPreview(adb, serial, configuration, preview.ArtifactGlob, project);
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Install",
                Kind = RunStepKind.Install,
                CommandLine = installCommand,
                WillRun = true,
            });
        }

        var packageName = TemplateEngine.Expand(
            configuration.Launch.PackageName ?? project.ApplicationId,
            context);
        var activity = TemplateEngine.Expand(
            configuration.Launch.Activity ?? project.LauncherActivity,
            context);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Launch",
                Kind = RunStepKind.Launch,
                CommandLine = string.Empty,
                WillRun = false,
                SkipReason = "No package name is configured for this project.",
            });
        }
        else
        {
            if (configuration.Launch.StopBeforeLaunch)
            {
                preview.Steps.Add(new RunPlanPreviewStep
                {
                    Name = "Force stop",
                    Kind = RunStepKind.Launch,
                    CommandLine = ProcessRequest.FormatCommandLine(
                        adb,
                        ["-s", serial, "shell", "am", "force-stop", packageName]),
                    WillRun = true,
                });
            }

            if (configuration.Logs.Attach && configuration.Logs.ClearBeforeLaunch)
            {
                preview.Steps.Add(new RunPlanPreviewStep
                {
                    Name = "Clear logcat",
                    Kind = RunStepKind.Logcat,
                    CommandLine = ProcessRequest.FormatCommandLine(adb, ["-s", serial, "logcat", "-c"]),
                    WillRun = true,
                });
            }

            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Launch",
                Kind = RunStepKind.Launch,
                CommandLine = BuildLaunchPreview(adb, serial, configuration, packageName, activity, context),
                WillRun = true,
            });
        }

        if (configuration.Logs.Attach)
        {
            var logArguments = new List<string> { "-s", serial, "logcat", "-v", "threadtime", "-v", "year", "-v", "uid", "-T", "1" };
            foreach (var buffer in configuration.Logs.Buffers)
            {
                logArguments.Add("-b");
                logArguments.Add(buffer);
            }

            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Attach logcat",
                Kind = RunStepKind.Logcat,
                CommandLine = ProcessRequest.FormatCommandLine(adb, logArguments),
                WillRun = !string.IsNullOrWhiteSpace(packageName),
                SkipReason = string.IsNullOrWhiteSpace(packageName)
                    ? "No package is associated with this run; logcat is not attached."
                    : null,
            });
        }

        if (configuration.Screen.Mode != ScreenMode.Off && configuration.Screen.AutoOpen)
        {
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Open mirror",
                Kind = RunStepKind.Mirror,
                CommandLine = $"scrcpy --serial {serial}",
                WillRun = true,
            });
        }

        foreach (var step in configuration.Steps)
        {
            var stepName = string.IsNullOrWhiteSpace(step.Name) ? step.Id : step.Name;
            if (!step.Enabled)
            {
                preview.Steps.Add(new RunPlanPreviewStep
                {
                    Name = stepName,
                    Kind = step.Kind,
                    WillRun = false,
                    SkipReason = "The step is disabled.",
                });
                continue;
            }

            var shouldRun = TemplateEngine.EvaluateCondition(step.Condition, context, out var reason);
            var command = TemplateEngine.Expand(step.Command, context);
            var arguments = TemplateEngine.Expand(step.Arguments, context);
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = stepName,
                Kind = step.Kind,
                CommandLine = string.IsNullOrWhiteSpace(command)
                    ? string.Empty
                    : ProcessRequest.FormatCommandLine(command, arguments),
                WillRun = shouldRun,
                SkipReason = shouldRun ? null : reason,
            });
        }

        return preview;
    }

    /// <inheritdoc />
    public ProcessRequest? CreateBuildRequest(ProjectModel project, RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Kind == RunConfigurationKind.Flutter)
        {
            return CreateFlutterBuildRequest(project, configuration);
        }

        if (configuration.Kind == RunConfigurationKind.Maui)
        {
            return CreateMauiBuildRequest(project, configuration);
        }

        if (configuration.Build.Skip || string.IsNullOrWhiteSpace(configuration.Build.Task))
        {
            return null;
        }

        var context = RunTemplateContext.ForProject(project, configuration);
        var root = project.RootPath;
        var workingDirectory = ResolveWorkingDirectory(project, configuration);
        var wrapper = FindWrapper(workingDirectory) ?? FindWrapper(root);
        var arguments = new List<string>();
        if (configuration.Build.Clean)
        {
            arguments.Add("clean");
        }

        arguments.Add(TemplateEngine.Expand(configuration.Build.Task, context));
        foreach (var argument in configuration.Build.Arguments)
        {
            arguments.Add(TemplateEngine.Expand(argument, context));
        }

        var environment = CreateBuildEnvironment(configuration, context);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.Build.TimeoutSeconds, 5, 24 * 60 * 60));
        if (wrapper is not null)
        {
            if (OperatingSystem.IsWindows() && wrapper.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                var commandLine = ProcessRequest.FormatCommandLine(wrapper, arguments);
                return new ProcessRequest
                {
                    FileName = "cmd.exe",
                    Arguments = ["/d", "/s", "/c", commandLine],
                    WorkingDirectory = workingDirectory,
                    Environment = environment,
                    Timeout = timeout,
                };
            }

            return new ProcessRequest
            {
                FileName = wrapper,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Environment = environment,
                Timeout = timeout,
            };
        }

        var gradle = ResolveGradle();
        return new ProcessRequest
        {
            FileName = gradle,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            Environment = environment,
            Timeout = timeout,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<RunBuildCommand> CreateBuildCommands(
        ProjectModel project,
        RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        var build = CreateBuildRequest(project, configuration);
        if (build is null)
        {
            return [];
        }

        var prepare = CreateHybridPrepareCommand(project, configuration);
        return prepare is null
            ? [new RunBuildCommand("Build", build)]
            : [prepare, new RunBuildCommand("Build", build)];
    }

    /// <inheritdoc />
    public ProcessRequest? CreateFlutterRunRequest(
        ProjectModel project,
        RunConfiguration configuration,
        string? deviceSerial = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(deviceSerial))
        {
            return null;
        }

        var context = RunTemplateContext.ForProject(project, configuration);
        var extraArguments = configuration.Build.Arguments
            .Select(argument => TemplateEngine.Expand(argument, context))
            .ToArray();
        return new ProcessRequest
        {
            FileName = ResolveFlutter(),
            Arguments = FlutterAdapter.CreateRunSessionArguments(
                deviceSerial,
                configuration.VariantName,
                extraArguments),
            WorkingDirectory = project.RootPath,
            Environment = CreateBuildEnvironment(configuration, context),
        };
    }

    /// <inheritdoc />
    public ProcessRequest? CreateMetroStartRequest(ProjectModel project, RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        if (project.Kind != ProjectKind.ReactNative)
        {
            return null;
        }

        var info = project.ReactNativeInfo;
        var isExpo = info?.IsExpo == true;
        var port = info?.MetroPort is > 0 and <= 65535
            ? info.MetroPort
            : ReactNativeProjectInfo.DefaultMetroPort;
        var context = RunTemplateContext.ForProject(project, configuration);
        var extraArguments = configuration.Build.Arguments
            .Select(argument => TemplateEngine.Expand(argument, context))
            .ToArray();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Keep the interactive keypress listener off; DroidDeck owns the Metro lifetime.
            ["CI"] = "1",
        };
        foreach (var (key, value) in configuration.Build.Environment)
        {
            environment[key] = TemplateEngine.Expand(value, context);
        }

        return new ProcessRequest
        {
            FileName = ResolveNpx(),
            Arguments = ReactNativeAdapter.CreateMetroStartArguments(isExpo, port, extraArguments),
            WorkingDirectory = project.RootPath,
            Environment = environment,
        };
    }

    private RunBuildCommand? CreateHybridPrepareCommand(ProjectModel project, RunConfiguration configuration)
    {
        if (configuration.Build.Skip)
        {
            return null;
        }

        string name;
        IReadOnlyList<string> arguments;
        switch (configuration.Kind)
        {
            case RunConfigurationKind.Capacitor:
                name = "Sync";
                arguments = CapacitorAdapter.CreateSyncArguments();
                break;
            case RunConfigurationKind.Cordova:
                name = "Prepare";
                arguments = CordovaAdapter.CreatePrepareArguments();
                break;
            default:
                return null;
        }

        var context = RunTemplateContext.ForProject(project, configuration);
        return new RunBuildCommand(name, new ProcessRequest
        {
            FileName = ResolveNpx(),
            Arguments = arguments,
            WorkingDirectory = project.RootPath,
            Environment = CreateBuildEnvironment(configuration, context),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.Build.TimeoutSeconds, 5, 24 * 60 * 60)),
        });
    }

    private ProcessRequest? CreateFlutterBuildRequest(ProjectModel project, RunConfiguration configuration)
    {
        if (configuration.Build.Skip)
        {
            return null;
        }

        var context = RunTemplateContext.ForProject(project, configuration);
        var extraArguments = configuration.Build.Arguments
            .Select(argument => TemplateEngine.Expand(argument, context))
            .ToArray();
        return new ProcessRequest
        {
            FileName = ResolveFlutter(),
            Arguments = FlutterAdapter.CreateBuildArguments(configuration.VariantName, extraArguments),
            WorkingDirectory = ResolveWorkingDirectory(project, configuration),
            Environment = CreateBuildEnvironment(configuration, context),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.Build.TimeoutSeconds, 5, 24 * 60 * 60)),
        };
    }

    private ProcessRequest? CreateMauiBuildRequest(ProjectModel project, RunConfiguration configuration)
    {
        if (configuration.Build.Skip)
        {
            return null;
        }

        var context = RunTemplateContext.ForProject(project, configuration);
        var projectFile = TemplateEngine.Expand(configuration.Build.Task, context);
        if (string.IsNullOrWhiteSpace(projectFile))
        {
            projectFile = project.MauiInfo?.ProjectFile;
            if (!string.IsNullOrWhiteSpace(projectFile) &&
                !string.IsNullOrWhiteSpace(configuration.Build.WorkingDirectory))
            {
                projectFile = Path.GetFileName(projectFile.Replace('\\', '/'));
            }
        }

        if (string.IsNullOrWhiteSpace(projectFile))
        {
            return null;
        }

        var extraArguments = configuration.Build.Arguments
            .Select(argument => TemplateEngine.Expand(argument, context))
            .ToArray();
        return new ProcessRequest
        {
            FileName = ResolveDotNet(),
            Arguments = MauiAdapter.CreateBuildArguments(
                projectFile,
                configuration.VariantName,
                configuration.Build.TargetFramework,
                extraArguments),
            WorkingDirectory = ResolveWorkingDirectory(project, configuration),
            Environment = CreateBuildEnvironment(configuration, context),
            Timeout = TimeSpan.FromSeconds(Math.Clamp(configuration.Build.TimeoutSeconds, 5, 24 * 60 * 60)),
        };
    }

    private static string ResolveWorkingDirectory(ProjectModel project, RunConfiguration configuration)
    {
        var root = project.RootPath;
        return string.IsNullOrWhiteSpace(configuration.Build.WorkingDirectory)
            ? root
            : Path.IsPathRooted(configuration.Build.WorkingDirectory)
                ? configuration.Build.WorkingDirectory
                : Path.Combine(root, configuration.Build.WorkingDirectory);
    }

    private static Dictionary<string, string> CreateBuildEnvironment(
        RunConfiguration configuration,
        RunTemplateContext context)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in configuration.Build.Environment)
        {
            environment[key] = TemplateEngine.Expand(value, context);
        }

        var javaHome = TemplateEngine.Expand(configuration.Build.JavaHome, context);
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            environment["JAVA_HOME"] = javaHome;
        }

        return environment;
    }

    private string ResolveFlutter()
    {
        var configured = _configuration?.Current.Settings.Tools.FlutterPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromSnapshot = _toolchain?.CachedSnapshot?.Get(ToolKind.Flutter)?.Path;
        return string.IsNullOrWhiteSpace(fromSnapshot) ? "flutter" : fromSnapshot;
    }

    private string ResolveGradle()
    {
        var configured = _configuration?.Current.Settings.Tools.GradlePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromSnapshot = _toolchain?.CachedSnapshot?.Get(ToolKind.Gradle)?.Path;
        return string.IsNullOrWhiteSpace(fromSnapshot) ? "gradle" : fromSnapshot;
    }

    private string ResolveNpx()
    {
        var fromSnapshot = _toolchain?.CachedSnapshot?.Get(ToolKind.Npx)?.Path;
        if (!string.IsNullOrWhiteSpace(fromSnapshot))
        {
            return fromSnapshot;
        }

        return OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
    }

    private string ResolveDotNet()
    {
        var configured = _configuration?.Current.Settings.Tools.DotNetPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromSnapshot = _toolchain?.CachedSnapshot?.Get(ToolKind.DotNet)?.Path;
        return string.IsNullOrWhiteSpace(fromSnapshot) ? "dotnet" : fromSnapshot;
    }

    private static string? FindWrapper(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var bat = Path.Combine(root, "gradlew.bat");
            if (File.Exists(bat))
            {
                return bat;
            }
        }

        var shell = Path.Combine(root, "gradlew");
        return File.Exists(shell) ? shell : null;
    }

    private static string BuildLaunchPreview(
        string adb,
        string serial,
        RunConfiguration configuration,
        string packageName,
        string activity,
        RunTemplateContext context)
    {
        var arguments = new List<string> { "-s", serial, "shell" };
        var component = string.IsNullOrWhiteSpace(activity)
            ? null
            : NormalizeComponent(packageName, activity);
        if (component is null)
        {
            arguments.AddRange([
                "monkey",
                "-p", packageName,
                "-c", "android.intent.category.LAUNCHER",
                "1",
            ]);
            return ProcessRequest.FormatCommandLine(adb, arguments);
        }

        arguments.Add("am");
        arguments.Add("start");
        arguments.Add("-W");
        if (!string.IsNullOrWhiteSpace(configuration.Launch.Action))
        {
            arguments.AddRange(["-a", TemplateEngine.Expand(configuration.Launch.Action, context)]);
        }

        if (!string.IsNullOrWhiteSpace(configuration.Launch.Data))
        {
            arguments.AddRange(["-d", TemplateEngine.Expand(configuration.Launch.Data, context)]);
        }

        arguments.AddRange(["-n", component]);
        if (configuration.Launch.WaitForDebugger)
        {
            arguments.Add("-D");
        }

        foreach (var extra in configuration.Launch.ExtraArguments)
        {
            arguments.Add(TemplateEngine.Expand(extra, context));
        }

        return ProcessRequest.FormatCommandLine(adb, arguments);
    }

    private static string NormalizeComponent(string packageName, string activity)
    {
        var component = activity.StartsWith('.') ? packageName + activity : activity;
        return component.Contains('/', StringComparison.Ordinal) ? component : packageName + "/" + component;
    }

    private static string BuildInstallPreview(
        string adb,
        string serial,
        RunConfiguration configuration,
        string artifactGlob,
        ProjectModel project)
    {
        var apk = project.Kind == ProjectKind.ApkOnly && !string.IsNullOrWhiteSpace(artifactGlob)
            ? artifactGlob
            : "<resolved APK>";
        var arguments = new List<string> { "-s", serial, "install" };
        if (project.ApkFiles.Any(reference => reference.IsSplit))
        {
            arguments = ["-s", serial, "install-multiple"];
        }

        if (configuration.Install.Replace)
        {
            arguments.Add("-r");
        }

        if (configuration.Install.TestOnly)
        {
            arguments.Add("-t");
        }

        if (configuration.Install.GrantPermissions)
        {
            arguments.Add("-g");
        }

        if (configuration.Install.AllowDowngrade)
        {
            arguments.Add("-d");
        }

        if (!string.IsNullOrWhiteSpace(configuration.Install.User))
        {
            arguments.AddRange(["--user", configuration.Install.User]);
        }

        arguments.AddRange(configuration.Install.ExtraArguments);
        arguments.Add(apk);
        return ProcessRequest.FormatCommandLine(adb, arguments);
    }
}
