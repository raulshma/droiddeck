namespace DroidDeck.Plugin.Abstractions;

/// <summary>The outcome of one plugin-provided test case.</summary>
public enum PluginTestStatus
{
    /// <summary>The test passed.</summary>
    Passed,

    /// <summary>The test failed an assertion.</summary>
    Failed,

    /// <summary>The test could not run to a result.</summary>
    Errored,

    /// <summary>The test was skipped or ignored.</summary>
    Skipped,
}

/// <summary>A failing test selected for a rerun.</summary>
/// <param name="ClassName">The declaring class or fixture, when known.</param>
/// <param name="Name">The test name.</param>
/// <param name="FilePath">The source file, when known.</param>
public sealed record TestRunnerTarget(string? ClassName, string Name, string? FilePath);

/// <summary>
/// Everything a plugin test runner needs to execute one run.
/// </summary>
public sealed record TestRunnerContext
{
    /// <summary>Gets the project root directory.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>Gets the project display name.</summary>
    public string? ProjectName { get; init; }

    /// <summary>Gets the target device serial, or <see langword="null"/> for a host run.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Gets the selected variant, when the project defines one.</summary>
    public string? VariantName { get; init; }

    /// <summary>Gets the selected module, when the project defines one.</summary>
    public string? ModuleName { get; init; }

    /// <summary>Gets the expected application package, when known.</summary>
    public string? PackageName { get; init; }

    /// <summary>Gets the failing tests to rerun; empty runs the full selection.</summary>
    public IReadOnlyList<TestRunnerTarget> FailedTests { get; init; } = [];

    /// <summary>Gets a writable directory the runner may keep results in.</summary>
    public string? ResultDirectory { get; init; }

    /// <summary>Gets additional runner settings supplied by the host.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One artifact attached to a plugin test case.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Path">The attachment file, when it is a file.</param>
/// <param name="Content">Inline text content, when the attachment is text.</param>
public sealed record PluginTestAttachment(string Name, string? Path = null, string? Content = null);

/// <summary>One test case reported by a plugin runner.</summary>
/// <param name="Name">The test name.</param>
/// <param name="Status">The outcome.</param>
/// <param name="ClassName">The declaring class or fixture, when known.</param>
/// <param name="DurationSeconds">How long the test took, in seconds.</param>
/// <param name="Message">The short failure message.</param>
/// <param name="StackTrace">The failure stack trace.</param>
/// <param name="FilePath">The source file, when known.</param>
/// <param name="Line">The source line, when known.</param>
/// <param name="Attachments">The case attachments.</param>
public sealed record PluginTestCase(
    string Name,
    PluginTestStatus Status,
    string? ClassName = null,
    double? DurationSeconds = null,
    string? Message = null,
    string? StackTrace = null,
    string? FilePath = null,
    int? Line = null,
    IReadOnlyList<PluginTestAttachment>? Attachments = null);

/// <summary>One test suite reported by a plugin runner.</summary>
/// <param name="Name">The suite name.</param>
/// <param name="Cases">The cases in reporting order.</param>
public sealed record PluginTestSuite(string Name, IReadOnlyList<PluginTestCase> Cases);

/// <summary>The structured result of one plugin test run.</summary>
/// <param name="Succeeded">Whether the run completed and produced usable results.</param>
/// <param name="Suites">The parsed suites.</param>
/// <param name="Output">Captured command output for the results panel.</param>
/// <param name="Error">The failure reason when the run did not produce results.</param>
public sealed record PluginTestRunResult(
    bool Succeeded,
    IReadOnlyList<PluginTestSuite>? Suites = null,
    string? Output = null,
    string? Error = null);

/// <summary>
/// A test runner contributed by a plugin. Built-in runners use this contract too.
/// </summary>
public interface ITestRunner
{
    /// <summary>Gets the stable runner identifier.</summary>
    string Id { get; }

    /// <summary>Gets the display name shown in the test UI.</summary>
    string Name { get; }

    /// <summary>Gets an optional description shown in the test UI.</summary>
    string? Description { get; }

    /// <summary>Gets whether the runner needs a connected device.</summary>
    bool RequiresDevice { get; }

    /// <summary>Gets whether a failed-only rerun can narrow the selection.</summary>
    bool SupportsRerun { get; }

    /// <summary>Runs the tests for one device or the host.</summary>
    Task<PluginTestRunResult> RunAsync(TestRunnerContext context, CancellationToken cancellationToken);
}
