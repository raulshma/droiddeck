using DroidDeck.Core.Configuration;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;

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

        if (configuration.Kind == RunConfigurationKind.Gradle &&
            !configuration.Build.Skip &&
            !string.IsNullOrWhiteSpace(configuration.Build.Task))
        {
            var request = CreateBuildRequest(project, configuration);
            preview.Steps.Add(new RunPlanPreviewStep
            {
                Name = "Build",
                Kind = RunStepKind.Build,
                CommandLine = request?.ToCommandLine() ?? "Build is skipped.",
                WillRun = request is not null,
                SkipReason = request is null ? "The build is disabled for this configuration." : null,
            });
        }
        else if (configuration.Kind == RunConfigurationKind.Gradle)
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

        if (!configuration.Install.ExtraArguments.Contains("--no-install", StringComparer.Ordinal))
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
        var launchArguments = new List<string> { "-s", serial, "shell", "am", "start", "-W" };
        if (!string.IsNullOrWhiteSpace(configuration.Launch.Action))
        {
            launchArguments.AddRange(["-a", TemplateEngine.Expand(configuration.Launch.Action, context)]);
        }

        if (!string.IsNullOrWhiteSpace(configuration.Launch.Data))
        {
            launchArguments.AddRange(["-d", TemplateEngine.Expand(configuration.Launch.Data, context)]);
        }

        if (!string.IsNullOrWhiteSpace(activity) && !string.IsNullOrWhiteSpace(packageName))
        {
            var component = activity.StartsWith('.') ? packageName + activity : activity;
            if (!component.Contains('/', StringComparison.Ordinal))
            {
                component = packageName + "/" + component;
            }

            launchArguments.AddRange(["-n", component]);
        }
        else if (!string.IsNullOrWhiteSpace(packageName))
        {
            launchArguments.AddRange([
                "-a", "android.intent.action.MAIN",
                "-c", "android.intent.category.LAUNCHER",
                "-p", packageName,
            ]);
        }

        if (configuration.Launch.WaitForDebugger)
        {
            launchArguments.Add("-D");
        }

        foreach (var extra in configuration.Launch.ExtraArguments)
        {
            launchArguments.Add(TemplateEngine.Expand(extra, context));
        }

        preview.Steps.Add(new RunPlanPreviewStep
        {
            Name = "Launch",
            Kind = RunStepKind.Launch,
            CommandLine = ProcessRequest.FormatCommandLine(adb, launchArguments),
            WillRun = true,
        });

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
                WillRun = true,
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
        if (configuration.Build.Skip || string.IsNullOrWhiteSpace(configuration.Build.Task))
        {
            return null;
        }

        var context = RunTemplateContext.ForProject(project, configuration);
        var root = project.RootPath;
        var wrapper = FindWrapper(root);
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

        var workingDirectory = string.IsNullOrWhiteSpace(configuration.Build.WorkingDirectory)
            ? root
            : Path.IsPathRooted(configuration.Build.WorkingDirectory)
                ? configuration.Build.WorkingDirectory
                : Path.Combine(root, configuration.Build.WorkingDirectory);
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
