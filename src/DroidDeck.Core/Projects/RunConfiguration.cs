using System.Text.Json.Serialization;

namespace DroidDeck.Core.Projects;

/// <summary>How a run configuration builds and launches an application.</summary>
public enum RunConfigurationKind
{
    /// <summary>A Gradle build followed by install and launch.</summary>
    Gradle,

    /// <summary>A Flutter build followed by install and launch.</summary>
    Flutter,

    /// <summary>A Gradle build inside the android host of a React Native project.</summary>
    ReactNative,

    /// <summary>A .NET MAUI build for an Android target framework.</summary>
    Maui,

    /// <summary>A Capacitor sync and Gradle build inside the android host.</summary>
    Capacitor,

    /// <summary>A Cordova prepare and Gradle build inside the android host.</summary>
    Cordova,

    /// <summary>An APK is installed directly without a build.</summary>
    Apk,

    /// <summary>User-defined steps drive the run.</summary>
    Custom,
}

/// <summary>The kind of step in a run plan.</summary>
public enum RunStepKind
{
    /// <summary>Runs a build command.</summary>
    Build,

    /// <summary>Installs an artifact on a device.</summary>
    Install,

    /// <summary>Launches the application.</summary>
    Launch,

    /// <summary>Attaches a log stream.</summary>
    Logcat,

    /// <summary>Opens the screen mirror.</summary>
    Mirror,

    /// <summary>Attaches a Flutter session or reloads the running Flutter app.</summary>
    HotReload,

    /// <summary>Starts or reuses the Metro development server for React Native.</summary>
    Metro,

    /// <summary>Waits for the device to be ready.</summary>
    WaitForDevice,

    /// <summary>Runs an arbitrary command.</summary>
    Custom,
}

/// <summary>What to do when a step fails.</summary>
public enum StepFailurePolicy
{
    /// <summary>Stop the session.</summary>
    Stop,

    /// <summary>Continue with the next step and mark the session partially failed.</summary>
    Continue,

    /// <summary>Retry the configured number of times, then stop.</summary>
    Retry,
}

/// <summary>Where the screen mirror should open.</summary>
public enum ScreenMode
{
    /// <summary>Do not open a mirror.</summary>
    Off,

    /// <summary>Open an external scrcpy window.</summary>
    External,

    /// <summary>Embed the mirror in the session workspace.</summary>
    Embedded,

    /// <summary>Dock the mirror beside the session workspace.</summary>
    Docked,
}

/// <summary>Build settings for a run configuration.</summary>
public sealed class BuildConfiguration
{
    /// <summary>Gets or sets the Gradle task or executable command.</summary>
    public string? Task { get; set; } = "assembleDebug";

    /// <summary>Gets or sets additional arguments passed to the build.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>Gets or sets environment variables applied to the build.</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the JDK home used for the build.</summary>
    public string? JavaHome { get; set; }

    /// <summary>Gets or sets the target framework for .NET builds, such as <c>net9.0-android</c>.</summary>
    public string? TargetFramework { get; set; }

    /// <summary>Gets or sets the build timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Gets or sets the working directory, relative to the project root.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets whether a clean task runs before the build.</summary>
    public bool Clean { get; set; }

    /// <summary>Gets or sets whether the build is skipped entirely.</summary>
    public bool Skip { get; set; }
}

/// <summary>Artifact discovery settings for a run configuration.</summary>
public sealed class ArtifactConfiguration
{
    /// <summary>Gets or sets the APK glob relative to the project root.</summary>
    public string Glob { get; set; } = string.Empty;

    /// <summary>Gets or sets whether split APKs in the artifact folder are installed together.</summary>
    public bool IncludeSplits { get; set; } = true;

    /// <summary>Gets or sets whether the artifact is inspected with aapt2.</summary>
    public bool InspectWithAapt2 { get; set; } = true;

    /// <summary>Gets or sets the expected package name.</summary>
    public string? PackageName { get; set; }
}

/// <summary>Install settings for a run configuration.</summary>
public sealed class InstallConfiguration
{
    /// <summary>Gets or sets whether an existing package is replaced.</summary>
    public bool Replace { get; set; } = true;

    /// <summary>Gets or sets whether test-only APKs are allowed.</summary>
    public bool TestOnly { get; set; } = true;

    /// <summary>Gets or sets whether runtime permissions are granted on install.</summary>
    public bool GrantPermissions { get; set; }

    /// <summary>Gets or sets whether a version downgrade is allowed.</summary>
    public bool AllowDowngrade { get; set; }

    /// <summary>Gets or sets the Android user to install for.</summary>
    public string? User { get; set; }

    /// <summary>Gets or sets additional adb install arguments.</summary>
    public List<string> ExtraArguments { get; set; } = [];
}

/// <summary>Launch settings for a run configuration.</summary>
public sealed class LaunchConfiguration
{
    /// <summary>Gets or sets the package name override.</summary>
    public string? PackageName { get; set; }

    /// <summary>Gets or sets the launcher activity override.</summary>
    public string? Activity { get; set; }

    /// <summary>Gets or sets the intent action override.</summary>
    public string? Action { get; set; }

    /// <summary>Gets or sets the intent data URI override.</summary>
    public string? Data { get; set; }

    /// <summary>Gets or sets extra <c>am start</c> arguments.</summary>
    public List<string> ExtraArguments { get; set; } = [];

    /// <summary>Gets or sets whether the launch waits for the first frame.</summary>
    public bool Wait { get; set; } = true;

    /// <summary>Gets or sets whether the app is force-stopped before launch.</summary>
    public bool StopBeforeLaunch { get; set; }

    /// <summary>Gets or sets whether the app waits for a debugger.</summary>
    public bool WaitForDebugger { get; set; }
}

/// <summary>Log settings for a run configuration.</summary>
public sealed class LogConfiguration
{
    /// <summary>Gets or sets the logcat buffers to attach.</summary>
    public List<string> Buffers { get; set; } = ["main", "system", "crash", "events"];

    /// <summary>Gets or sets whether logcat is cleared before launch.</summary>
    public bool ClearBeforeLaunch { get; set; } = true;

    /// <summary>Gets or sets whether host-side app-only filtering is applied.</summary>
    public bool AppOnly { get; set; } = true;

    /// <summary>Gets or sets the maximum number of lines retained in memory.</summary>
    public int MaxLines { get; set; } = 20_000;

    /// <summary>Gets or sets whether logcat is attached automatically.</summary>
    public bool Attach { get; set; } = true;
}

/// <summary>Screen settings for a run configuration.</summary>
public sealed class ScreenConfiguration
{
    /// <summary>Gets or sets the mirror mode.</summary>
    public ScreenMode Mode { get; set; } = ScreenMode.Off;

    /// <summary>Gets or sets whether the mirror opens automatically after launch.</summary>
    public bool AutoOpen { get; set; }
}

/// <summary>A user or plugin defined step.</summary>
public sealed class RunStepDefinition
{
    /// <summary>Gets or sets the stable step identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the step kind.</summary>
    public RunStepKind Kind { get; set; } = RunStepKind.Custom;

    /// <summary>Gets or sets the executable or script path.</summary>
    public string? Command { get; set; }

    /// <summary>
    /// Gets or sets the identifier of a registered run step (built-in or plugin)
    /// to execute. When set, <see cref="Command"/> is ignored.
    /// </summary>
    public string? StepId { get; set; }

    /// <summary>Gets or sets the arguments. Placeholders are expanded before execution.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>Gets or sets the working directory relative to the project root.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets environment variables for the step.</summary>
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the step timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Gets or sets an optional condition that decides whether the step runs.</summary>
    public string? Condition { get; set; }

    /// <summary>Gets or sets the failure policy.</summary>
    public StepFailurePolicy FailurePolicy { get; set; } = StepFailurePolicy.Stop;

    /// <summary>Gets or sets the number of retries for the retry policy.</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>Gets or sets whether the step is enabled.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>A saved run configuration.</summary>
public sealed class RunConfiguration
{
    /// <summary>Gets or sets the stable configuration identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the configuration kind.</summary>
    public RunConfigurationKind Kind { get; set; } = RunConfigurationKind.Gradle;

    /// <summary>Gets or sets the selected variant.</summary>
    public string? VariantName { get; set; }

    /// <summary>Gets or sets the selected module.</summary>
    public string? ModuleName { get; set; }

    /// <summary>Gets or sets the target device serial; empty means the active device.</summary>
    public string? DeviceSerial { get; set; }

    /// <summary>Gets or sets the build settings.</summary>
    public BuildConfiguration Build { get; set; } = new();

    /// <summary>Gets or sets the artifact settings.</summary>
    public ArtifactConfiguration Artifact { get; set; } = new();

    /// <summary>Gets or sets the install settings.</summary>
    public InstallConfiguration Install { get; set; } = new();

    /// <summary>Gets or sets the launch settings.</summary>
    public LaunchConfiguration Launch { get; set; } = new();

    /// <summary>Gets or sets the log settings.</summary>
    public LogConfiguration Logs { get; set; } = new();

    /// <summary>Gets or sets the screen settings.</summary>
    public ScreenConfiguration Screen { get; set; } = new();

    /// <summary>Gets or sets custom steps that run after the built-in pipeline.</summary>
    public List<RunStepDefinition> Steps { get; set; } = [];

    /// <summary>Gets or sets whether this configuration is the project default.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; set; }

    /// <summary>Returns a deep copy of the configuration.</summary>
    public RunConfiguration Clone()
    {
        var clone = DroidDeck.Core.Infrastructure.DroidDeckJson.Deserialize<RunConfiguration>(
            DroidDeck.Core.Infrastructure.DroidDeckJson.Serialize(this));
        return clone ?? throw new InvalidOperationException("The run configuration could not be copied.");
    }
}

/// <summary>A field-level validation problem in a run configuration.</summary>
/// <param name="Field">The field path, such as <c>build.task</c>.</param>
/// <param name="Message">The user-facing explanation.</param>
public sealed record RunConfigurationValidationError(string Field, string Message);

/// <summary>The result of validating a run configuration.</summary>
public sealed class RunConfigurationValidationResult
{
    /// <summary>Gets or sets the validation errors.</summary>
    public List<RunConfigurationValidationError> Errors { get; set; } = [];

    /// <summary>Gets whether the configuration can be saved.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates run configurations before they are saved.</summary>
public static class RunConfigurationValidator
{
    /// <summary>Validates a configuration against the project it belongs to.</summary>
    public static RunConfigurationValidationResult Validate(ProjectModel project, RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<RunConfigurationValidationError>();

        if (string.IsNullOrWhiteSpace(configuration.Name))
        {
            errors.Add(new("name", "A configuration name is required."));
        }

        if (configuration.Kind is RunConfigurationKind.Gradle
            or RunConfigurationKind.ReactNative
            or RunConfigurationKind.Capacitor
            or RunConfigurationKind.Cordova)
        {
            if (string.IsNullOrWhiteSpace(configuration.Build.Task))
            {
                errors.Add(new("build.task", "A Gradle task is required."));
            }

            if (string.IsNullOrWhiteSpace(configuration.Artifact.Glob))
            {
                errors.Add(new("artifact.glob", "An artifact glob is required so the built APK can be found."));
            }
        }
        else if (configuration.Kind == RunConfigurationKind.Flutter)
        {
            if (string.IsNullOrWhiteSpace(configuration.Artifact.Glob))
            {
                errors.Add(new("artifact.glob", "An artifact glob is required so the built APK can be found."));
            }
        }
        else if (configuration.Kind == RunConfigurationKind.Maui)
        {
            if (string.IsNullOrWhiteSpace(configuration.Build.Task))
            {
                errors.Add(new("build.task", "A project file is required for the MAUI build."));
            }

            if (string.IsNullOrWhiteSpace(configuration.Artifact.Glob))
            {
                errors.Add(new("artifact.glob", "An artifact glob is required so the built APK can be found."));
            }

            if (string.IsNullOrWhiteSpace(configuration.Build.TargetFramework))
            {
                errors.Add(new(
                    "build.targetFramework",
                    "An Android target framework is required, such as net9.0-android."));
            }
        }
        else if (configuration.Kind == RunConfigurationKind.Apk)
        {
            if (project.ApkFiles.Count == 0 && string.IsNullOrWhiteSpace(configuration.Artifact.Glob))
            {
                errors.Add(new("artifact.glob", "Add an APK or specify an artifact glob."));
            }
        }
        else if (configuration.Kind == RunConfigurationKind.Custom &&
                 configuration.Steps.All(step =>
                     !step.Enabled ||
                     (string.IsNullOrWhiteSpace(step.Command) && string.IsNullOrWhiteSpace(step.StepId))))
        {
            errors.Add(new("steps", "A custom configuration needs at least one enabled step with a command or run step."));
        }

        if (configuration.Build.TimeoutSeconds is < 5 or > 24 * 60 * 60)
        {
            errors.Add(new("build.timeoutSeconds", "The build timeout must be between 5 seconds and 24 hours."));
        }

        if (configuration.Logs.MaxLines is < 100 or > 1_000_000)
        {
            errors.Add(new("logs.maxLines", "The log buffer must hold between 100 and 1,000,000 lines."));
        }

        foreach (var step in configuration.Steps)
        {
            if (step.Enabled &&
                step.Kind == RunStepKind.Custom &&
                string.IsNullOrWhiteSpace(step.Command) &&
                string.IsNullOrWhiteSpace(step.StepId))
            {
                errors.Add(new($"steps.{step.Id}.command", $"Step \"{step.Name}\" needs a command or run step."));
            }

            if (step.TimeoutSeconds < 1)
            {
                errors.Add(new($"steps.{step.Id}.timeoutSeconds", $"Step \"{step.Name}\" needs a positive timeout."));
            }
        }

        return new RunConfigurationValidationResult { Errors = errors };
    }
}
