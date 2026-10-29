using DroidDeck.Plugin.Abstractions;

namespace SampleCommandPlugin;

/// <summary>
/// A minimal plugin that registers one command.
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private string _dataDirectory = string.Empty;
    private string _pluginId = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        _pluginId = context.Info.Id;
        await context.Settings.SetAsync("initialized", "true", cancellationToken).ConfigureAwait(false);
        await AppendLifecycleMarkerAsync("init", cancellationToken).ConfigureAwait(false);
        context.Extensions.AddCommand(new SampleCommand(context.DataDirectory));
        context.Extensions.AddDeviceAction(new SampleDeviceAction(context.DataDirectory));
        context.Extensions.AddToolbarAction(new SampleToolbarAction(context.DataDirectory));
        context.Extensions.AddDeviceTab(new SampleDeviceTab());
        context.Extensions.AddRunStep(new SampleRunStep(context.DataDirectory));
        context.Extensions.AddLogParser(new SampleLogParser());
        context.Extensions.AddHighlightRuleProvider(new SampleHighlightRuleProvider());
        context.Extensions.AddTestRunner(new SampleTestRunner(context.DataDirectory));
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_dataDirectory.Length > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_dataDirectory, "shutdown.txt"),
                "ok",
                cancellationToken).ConfigureAwait(false);
        }

        if (_pluginId.Length > 0)
        {
            await AppendLifecycleMarkerAsync("shutdown", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends a marker to a shared log next to the plugin directories so tests
    /// can observe the order in which the host initializes and shuts plugins down.
    /// </summary>
    private async Task AppendLifecycleMarkerAsync(string phase, CancellationToken cancellationToken)
    {
        var logPath = Path.GetFullPath(Path.Combine(_dataDirectory, "..", "..", "lifecycle-order.log"));
        await File.AppendAllTextAsync(
            logPath,
            $"{_pluginId}:{phase}{Environment.NewLine}",
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A command that writes a marker file so tests can observe execution.
/// </summary>
public sealed class SampleCommand(string dataDirectory) : ICommand
{
    /// <inheritdoc />
    public string Id => "com.example.sample.command";

    /// <inheritdoc />
    public string Title => "Run sample command";

    /// <inheritdoc />
    public string Description => "Writes a marker file that proves the sample plugin command ran.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords => ["sample", "plugin", "command"];

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "executed.txt"),
            "ok",
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A device action that writes a marker file so tests and the host can observe it.
/// </summary>
public sealed class SampleDeviceAction(string dataDirectory) : IDeviceAction
{
    /// <inheritdoc />
    public string Id => "com.example.sample.deviceAction";

    /// <inheritdoc />
    public string Name => "Run sample device action";

    /// <inheritdoc />
    public bool RequiresConfirmation => false;

    /// <inheritdoc />
    public async Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, $"device-action-{Sanitize(context.Serial)}.txt"),
            context.Model ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
}

/// <summary>
/// A toolbar action that writes a marker file so tests can observe execution.
/// </summary>
public sealed class SampleToolbarAction(string dataDirectory) : IDeviceToolbarAction
{
    /// <inheritdoc />
    public string Id => "com.example.sample.toolbarAction";

    /// <inheritdoc />
    public string Title => "Sample toolbar action";

    /// <inheritdoc />
    public string? ToolTip => "Proves the plugin toolbar path.";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public bool RequiresConfirmation => false;

    /// <inheritdoc />
    public async Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "toolbar-action.txt"),
            context.Serial,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A plugin device tab. The host renders the returned content.
/// </summary>
public sealed class SampleDeviceTab : IDeviceTabProvider
{
    /// <inheritdoc />
    public string Id => "com.example.sample.deviceTab";

    /// <inheritdoc />
    public string Title => "Sample tab";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public object CreateContent(DeviceTabContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"Sample plugin tab for {context.Model ?? context.Serial}.";
    }
}

/// <summary>
/// A run step that writes a marker file so tests can observe the pipeline call.
/// </summary>
public sealed class SampleRunStep(string dataDirectory) : IRunStep
{
    /// <inheritdoc />
    public string Id => "com.example.sample.runStep";

    /// <inheritdoc />
    public async Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "run-step.txt"),
            $"{context.ProjectRoot}|{context.DeviceSerial}",
            cancellationToken).ConfigureAwait(false);
        return new RunStepResult(true, "Sample run step completed.");
    }
}

/// <summary>
/// A log parser that tags the sample marker lines and points them at the sample highlight rule.
/// </summary>
public sealed class SampleLogParser : ILogParser
{
    /// <inheritdoc />
    public string Id => "com.example.sample.logParser";

    /// <inheritdoc />
    public string Name => "Sample log parser";

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public LogParserResult? Parse(LogParserContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RawLine.Contains("[sample]", StringComparison.Ordinal)
            ? new LogParserResult(Tag: "Sample", HighlightRuleId: SampleHighlightRuleProvider.RuleId)
            : null;
    }
}

/// <summary>
/// A highlight rule provider that mirrors the rule the sample parser selects.
/// </summary>
public sealed class SampleHighlightRuleProvider : IHighlightRuleProvider
{
    /// <summary>The stable identifier of the sample highlight rule.</summary>
    public const string RuleId = "com.example.sample.highlight";

    /// <inheritdoc />
    public string Id => "com.example.sample.highlightRules";

    /// <inheritdoc />
    public IReadOnlyList<PluginHighlightRule> Rules { get; } =
        [new PluginHighlightRule(RuleId, "Sample highlight", "[sample]")];
}

/// <summary>
/// A test runner that produces a deterministic suite so tests can observe results.
/// </summary>
public sealed class SampleTestRunner(string dataDirectory) : ITestRunner
{
    /// <inheritdoc />
    public string Id => "com.example.sample.testRunner";

    /// <inheritdoc />
    public string Name => "Sample test runner";

    /// <inheritdoc />
    public string? Description => "Produces a deterministic sample suite without running a toolchain.";

    /// <inheritdoc />
    public bool RequiresDevice => false;

    /// <inheritdoc />
    public bool SupportsRerun => true;

    /// <inheritdoc />
    public async Task<PluginTestRunResult> RunAsync(TestRunnerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "test-run.txt"),
            context.ProjectRoot,
            cancellationToken).ConfigureAwait(false);
        var cases = context.FailedTests.Count > 0
            ? context.FailedTests
                .Select(target => new PluginTestCase(target.Name, PluginTestStatus.Passed, target.ClassName))
                .ToArray()
            : [new PluginTestCase("SamplePasses", PluginTestStatus.Passed, "SampleTests")];
        return new PluginTestRunResult(
            Succeeded: true,
            Suites: [new PluginTestSuite("Sample suite", cases)],
            Output: "Sample runner completed.");
    }
}

/// <summary>
/// A type that is deliberately not a plugin, used to test entry type validation.
/// </summary>
public sealed class NotAPlugin
{
}

/// <summary>
/// A plugin that fails during initialization, used to test load failure isolation.
/// </summary>
public sealed class ThrowingPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Extensions.AddCommand(new ThrowingCommand());
        throw new InvalidOperationException("This plugin always fails to initialize.");
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A plugin that throws while constructing, used to test constructor fault isolation.
/// </summary>
public sealed class ConstructorThrowingPlugin : IPlugin
{
    public ConstructorThrowingPlugin()
    {
        throw new InvalidOperationException("This plugin always fails to construct.");
    }

    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("This plugin never initializes."));

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A command registered by the throwing plugin before it fails.
/// </summary>
public sealed class ThrowingCommand : ICommand
{
    /// <inheritdoc />
    public string Id => "com.example.throwing.command";

    /// <inheritdoc />
    public string Title => "Never available";

    /// <inheritdoc />
    public string Description => "Registered by the throwing plugin before initialization fails.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords => [];

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
