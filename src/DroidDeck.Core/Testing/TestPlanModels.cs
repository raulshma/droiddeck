using System.Text.Json.Serialization;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Testing;

/// <summary>The built-in test workflows DroidDeck can execute.</summary>
public enum TestRunnerKind
{
    /// <summary>Gradle JVM unit tests.</summary>
    GradleUnit,

    /// <summary>Gradle connected Android instrumentation tests.</summary>
    GradleConnected,

    /// <summary><c>flutter test</c> unit and widget tests.</summary>
    Flutter,

    /// <summary>Jest tests in a React Native project.</summary>
    Jest,

    /// <summary><c>dotnet test</c> against a discovered test project.</summary>
    DotNet,

    /// <summary>A user-supplied command that writes JUnit XML.</summary>
    Custom,

    /// <summary>A test runner registered by a plugin or built in.</summary>
    Plugin,
}

/// <summary>One failing test selected for a rerun.</summary>
public sealed class TestTarget
{
    /// <summary>Gets or sets the declaring class or fixture.</summary>
    public string? ClassName { get; init; }

    /// <summary>Gets or sets the test name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the source file, when the runner reports one.</summary>
    public string? FilePath { get; init; }

    /// <summary>Gets the fully qualified name.</summary>
    [JsonIgnore]
    public string FullyQualifiedName => string.IsNullOrWhiteSpace(ClassName) ? Name : $"{ClassName}.{Name}";
}

/// <summary>Settings for a user-defined test command.</summary>
public sealed class CustomTestSettings
{
    /// <summary>Gets or sets the executable.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Gets or sets the arguments. <c>${failedTests}</c>, <c>${failedFiles}</c>, and
    /// <c>${failedClasses}</c> expand during a failed-only rerun.</summary>
    public List<string> Arguments { get; set; } = [];

    /// <summary>Gets or sets the working directory, relative to the project root.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Gets or sets the JUnit XML glob or file, relative to the project root.</summary>
    public string? ResultGlob { get; set; }

    /// <summary>Gets or sets whether the command needs a device.</summary>
    public bool RequiresDevice { get; set; }
}

/// <summary>A request to build the commands for one test run.</summary>
public sealed class TestPlanRequest
{
    /// <summary>Gets or sets the project.</summary>
    public required ProjectModel Project { get; init; }

    /// <summary>Gets or sets the run configuration that supplies the variant, module, and tools.</summary>
    public required RunConfiguration Configuration { get; init; }

    /// <summary>Gets or sets the selected runner.</summary>
    public TestRunnerKind Runner { get; init; }

    /// <summary>Gets or sets the device serial for device-bound runners.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Gets or sets the failing tests to rerun; empty runs the full selection.</summary>
    public IReadOnlyList<TestTarget> FailedTests { get; init; } = [];

    /// <summary>Gets or sets a directory the command may write structured results into.</summary>
    public string? ResultDirectory { get; init; }

    /// <summary>Gets or sets the custom command settings.</summary>
    public CustomTestSettings? Custom { get; init; }
}

/// <summary>One command that produces structured test results.</summary>
public sealed class TestPlanCommand
{
    /// <summary>Gets or sets the step name.</summary>
    public string Name { get; set; } = "Run tests";

    /// <summary>Gets or sets the process to run.</summary>
    public required ProcessRequest Request { get; set; }

    /// <summary>Gets or sets the result format.</summary>
    public TestResultFormat Format { get; set; }

    /// <summary>Gets or sets the result file, directory, or glob.</summary>
    public string? ResultPath { get; set; }

    /// <summary>Gets or sets whether the results are read from standard output.</summary>
    public bool ReadFromStandardOutput { get; set; }

    /// <summary>Gets or sets whether the command needs a device.</summary>
    public bool RequiresDevice { get; set; }
}

/// <summary>The commands and decisions for one test run.</summary>
public sealed class TestPlan
{
    /// <summary>Gets or sets the selected runner.</summary>
    public TestRunnerKind Runner { get; set; }

    /// <summary>Gets or sets the commands in execution order.</summary>
    public List<TestPlanCommand> Commands { get; set; } = [];

    /// <summary>Gets or sets validation or discovery problems.</summary>
    public List<string> Errors { get; set; } = [];

    /// <summary>Gets or sets whether a failed-only rerun can narrow the selection.</summary>
    public bool SupportsRerun { get; set; }

    /// <summary>Gets whether the plan can execute.</summary>
    [JsonIgnore]
    public bool IsValid => Errors.Count == 0 && Commands.Count > 0;
}

/// <summary>Builds the commands a test run will execute for a project and runner.</summary>
public interface ITestPlanBuilder
{
    /// <summary>Builds the plan for one device or the host.</summary>
    TestPlan Build(TestPlanRequest request);
}
