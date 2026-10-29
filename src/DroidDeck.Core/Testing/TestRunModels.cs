using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Testing;

/// <summary>Whether a test run finished and produced readable results.</summary>
public enum TestRunOutcome
{
    /// <summary>Every command finished and its results were parsed.</summary>
    Passed,

    /// <summary>The run failed, was misconfigured, or produced unreadable results.</summary>
    Failed,

    /// <summary>The run was cancelled.</summary>
    Cancelled,
}

/// <summary>The kind of progress message reported during a test run.</summary>
public enum TestRunProgressKind
{
    /// <summary>A command is about to run.</summary>
    Started,

    /// <summary>A command finished.</summary>
    Completed,
}

/// <summary>Progress reported while a test run executes.</summary>
public sealed class TestRunProgress
{
    /// <summary>Gets or sets the device the message belongs to; <see langword="null"/> for a host run.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Gets or sets the user-facing message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets or sets the progress kind.</summary>
    public TestRunProgressKind Kind { get; init; }
}

/// <summary>A request to execute the tests for a project.</summary>
public sealed class TestRunRequest
{
    /// <summary>Gets or sets the project.</summary>
    public required ProjectModel Project { get; init; }

    /// <summary>Gets or sets the run configuration that supplies the variant, module, and tools.</summary>
    public required RunConfiguration Configuration { get; init; }

    /// <summary>Gets or sets the runner.</summary>
    public TestRunnerKind Runner { get; init; }

    /// <summary>Gets or sets the devices to run on. Empty runs on the host for host-only runners.</summary>
    public IReadOnlyList<string> DeviceSerials { get; init; } = [];

    /// <summary>Gets or sets the failing tests to rerun; empty runs the full selection.</summary>
    public IReadOnlyList<TestTarget> FailedTests { get; init; } = [];

    /// <summary>Gets or sets the custom command settings.</summary>
    public CustomTestSettings? Custom { get; init; }

    /// <summary>Gets whether this request narrows the run to previously failing tests.</summary>
    public bool IsRerun => FailedTests.Count > 0;
}

/// <summary>The parsed results and diagnostics for one device or the host.</summary>
public sealed class TestRunDeviceResult
{
    /// <summary>Gets or sets the device serial, or <see langword="null"/> for a host run.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Gets or sets whether the run finished and produced readable results.</summary>
    public TestRunOutcome Outcome { get; set; } = TestRunOutcome.Failed;

    /// <summary>Gets or sets the parsed suites.</summary>
    public List<TestSuiteResult> Suites { get; set; } = [];

    /// <summary>Gets or sets configuration, process, or parse errors.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Gets or sets the captured output of every command.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>Gets the number of passed cases.</summary>
    public int PassedCount => Suites.Sum(suite => suite.PassedCount);

    /// <summary>Gets the number of failed cases.</summary>
    public int FailedCount => Suites.Sum(suite => suite.FailedCount);

    /// <summary>Gets the number of errored cases.</summary>
    public int ErroredCount => Suites.Sum(suite => suite.ErroredCount);

    /// <summary>Gets the number of skipped cases.</summary>
    public int SkippedCount => Suites.Sum(suite => suite.SkippedCount);

    /// <summary>Gets the total number of cases.</summary>
    public int TotalCount => Suites.Sum(suite => suite.Cases.Count);

    /// <summary>Gets whether any case did not pass.</summary>
    public bool HasFailures => Suites.Any(suite => suite.FailedCount > 0 || suite.ErroredCount > 0);

    /// <summary>Gets the display name for the device or host.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(DeviceSerial) ? "Host" : DeviceSerial;
}

/// <summary>The result of a test run across every selected device.</summary>
public sealed class TestRunSummary
{
    /// <summary>Gets or sets the runner that executed.</summary>
    public TestRunnerKind Runner { get; set; }

    /// <summary>Gets or sets the per-device results.</summary>
    public List<TestRunDeviceResult> Devices { get; set; } = [];

    /// <summary>Gets or sets whether the run was cancelled.</summary>
    public bool IsCanceled { get; set; }

    /// <summary>Gets the number of passed cases across devices.</summary>
    public int PassedCount => Devices.Sum(device => device.PassedCount);

    /// <summary>Gets the number of failed cases across devices.</summary>
    public int FailedCount => Devices.Sum(device => device.FailedCount);

    /// <summary>Gets the number of errored cases across devices.</summary>
    public int ErroredCount => Devices.Sum(device => device.ErroredCount);

    /// <summary>Gets the number of skipped cases across devices.</summary>
    public int SkippedCount => Devices.Sum(device => device.SkippedCount);

    /// <summary>Gets the total number of cases across devices.</summary>
    public int TotalCount => Devices.Sum(device => device.TotalCount);

    /// <summary>
    /// Gets whether every device finished and produced readable results. Failing
    /// test cases do not make the run unsuccessful; check the failure counts.
    /// </summary>
    public bool IsSuccess =>
        !IsCanceled &&
        Devices.Count > 0 &&
        Devices.All(device => device.Outcome == TestRunOutcome.Passed);

    /// <summary>Collects the failing cases as rerun targets, deduplicated across devices.</summary>
    public List<TestTarget> CollectFailedTests()
    {
        var targets = new List<TestTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var test in Devices.SelectMany(device => device.Suites).SelectMany(suite => suite.Cases))
        {
            if (!test.IsFailure)
            {
                continue;
            }

            var key = $"{test.FullyQualifiedName}|{test.FilePath}";
            if (seen.Add(key))
            {
                targets.Add(new TestTarget
                {
                    ClassName = test.ClassName,
                    Name = test.Name,
                    FilePath = test.FilePath,
                });
            }
        }

        return targets;
    }
}

/// <summary>Runs tests for a project and parses their structured results.</summary>
public interface ITestRunService
{
    /// <summary>Executes the plan on every selected device and returns the parsed results.</summary>
    Task<TestRunSummary> RunAsync(
        TestRunRequest request,
        IProgress<TestRunProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
