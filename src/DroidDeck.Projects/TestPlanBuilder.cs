using System.Text.RegularExpressions;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Testing;
using DroidDeck.Core.Toolchain;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects;

/// <summary>Builds the commands a test run executes for the supported runners.</summary>
public sealed class TestPlanBuilder : ITestPlanBuilder
{
    private readonly IRunPlanBuilder _runPlans;
    private readonly IConfigurationService? _configuration;
    private readonly IToolchainService? _toolchain;

    /// <summary>Creates a builder that reuses the run pipeline for build and tool resolution.</summary>
    public TestPlanBuilder(
        IRunPlanBuilder? runPlans = null,
        IConfigurationService? configuration = null,
        IToolchainService? toolchain = null)
    {
        _configuration = configuration;
        _toolchain = toolchain;
        _runPlans = runPlans ?? new RunPlanBuilder(configuration, toolchain);
    }

    /// <inheritdoc />
    public TestPlan Build(TestPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = new TestPlan { Runner = request.Runner };
        switch (request.Runner)
        {
            case TestRunnerKind.GradleUnit:
                BuildGradle(request, plan, connected: false);
                break;
            case TestRunnerKind.GradleConnected:
                BuildGradle(request, plan, connected: true);
                break;
            case TestRunnerKind.Flutter:
                BuildFlutter(request, plan);
                break;
            case TestRunnerKind.Jest:
                BuildJest(request, plan);
                break;
            case TestRunnerKind.DotNet:
                BuildDotNet(request, plan);
                break;
            case TestRunnerKind.Custom:
                BuildCustom(request, plan);
                break;
            case TestRunnerKind.Plugin:
                plan.Errors.Add("Select a registered test runner before running tests.");
                break;
            default:
                plan.Errors.Add($"The {request.Runner} runner is not supported yet.");
                break;
        }

        return plan;
    }

    private void BuildGradle(TestPlanRequest request, TestPlan plan, bool connected)
    {
        if (connected && string.IsNullOrWhiteSpace(request.DeviceSerial))
        {
            plan.Errors.Add("Select a device before running instrumentation tests.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Configuration.VariantName))
        {
            plan.Errors.Add("Select a build variant before running Gradle tests.");
            return;
        }

        var module = request.Configuration.ModuleName ?? request.Project.ApplicationModule;
        var prefix = string.IsNullOrWhiteSpace(module) ? ":" : $":{module.TrimStart(':')}:";
        var variant = Capitalize(request.Configuration.VariantName);
        var task = connected
            ? $"{prefix}connected{variant}AndroidTest"
            : $"{prefix}test{variant}UnitTest";
        var configuration = request.Configuration.Clone();
        configuration.Build.Task = task;
        configuration.Build.Clean = false;
        configuration.Build.Skip = false;
        configuration.Build.Arguments =
        [
            .. configuration.Build.Arguments,
            .. CreateRerunArguments(request.FailedTests, connected),
        ];

        var build = _runPlans.CreateBuildRequest(request.Project, configuration);
        if (build is null)
        {
            plan.Errors.Add("The Gradle build could not be prepared for this configuration.");
            return;
        }

        if (connected)
        {
            var environment = new Dictionary<string, string>(build.Environment, StringComparer.OrdinalIgnoreCase)
            {
                ["ANDROID_SERIAL"] = request.DeviceSerial!,
            };
            build = build with { Environment = environment };
        }

        plan.Commands.Add(new TestPlanCommand
        {
            Name = connected ? "Run instrumentation tests" : "Run unit tests",
            Request = build,
            Format = TestResultFormat.JUnitXml,
            ResultPath = connected
                ? "**/build/outputs/androidTest-results/**/*.xml"
                : $"**/build/test-results/test{variant}UnitTest/*.xml",
            RequiresDevice = connected,
        });
        plan.SupportsRerun = true;
    }

    private void BuildFlutter(TestPlanRequest request, TestPlan plan)
    {
        var arguments = new List<string>(FlutterAdapter.CreateTestArguments());
        var paths = DistinctFiles(request.FailedTests);
        if (paths.Count > 0)
        {
            arguments.AddRange(paths);
        }
        else
        {
            var names = DistinctNames(request.FailedTests);
            if (names.Count > 0)
            {
                arguments.Add($"--name=^(?:{string.Join('|', names)})$");
            }
        }

        plan.Commands.Add(new TestPlanCommand
        {
            Name = "Run Flutter tests",
            Request = new ProcessRequest
            {
                FileName = ResolveFlutter(),
                Arguments = arguments,
                WorkingDirectory = request.Project.RootPath,
            },
            Format = TestResultFormat.FlutterJson,
            ReadFromStandardOutput = true,
        });
        plan.SupportsRerun = true;
    }

    private void BuildJest(TestPlanRequest request, TestPlan plan)
    {
        var resultsDirectory = string.IsNullOrWhiteSpace(request.ResultDirectory)
            ? Path.Combine(Path.GetTempPath(), "droiddeck-tests")
            : request.ResultDirectory;
        var outputFile = Path.Combine(resultsDirectory, "jest-results.json");
        var arguments = new List<string> { "jest", "--json", $"--outputFile={outputFile}" };
        var paths = DistinctFiles(request.FailedTests);
        if (paths.Count > 0)
        {
            arguments.Add("--runTestsByPath");
            arguments.AddRange(paths);
        }
        else
        {
            var names = DistinctNames(request.FailedTests);
            if (names.Count > 0)
            {
                arguments.Add("-t");
                arguments.Add(string.Join('|', names));
            }
        }

        plan.Commands.Add(new TestPlanCommand
        {
            Name = "Run Jest tests",
            Request = new ProcessRequest
            {
                FileName = ResolveNpx(),
                Arguments = arguments,
                WorkingDirectory = request.Project.RootPath,
            },
            Format = TestResultFormat.JestJson,
            ResultPath = outputFile,
        });
        plan.SupportsRerun = true;
    }

    private void BuildDotNet(TestPlanRequest request, TestPlan plan)
    {
        var task = request.Configuration.Build.Task;
        var projectFile = IsProjectFile(task) ? task : request.Project.MauiInfo?.ProjectFile;
        if (string.IsNullOrWhiteSpace(projectFile))
        {
            plan.Errors.Add("A test project file is required for .NET tests.");
            return;
        }

        var resultsDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(request.ResultDirectory)
                ? Path.Combine(Path.GetTempPath(), "droiddeck-tests")
                : request.ResultDirectory,
            "trx");
        var arguments = new List<string> { "test", projectFile };
        var targetFramework = request.Configuration.Build.TargetFramework;
        if (string.IsNullOrWhiteSpace(targetFramework))
        {
            targetFramework = request.Project.MauiInfo?.AndroidTargetFrameworks.FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            arguments.Add("-f");
            arguments.Add(targetFramework);
        }

        arguments.Add("--results-directory");
        arguments.Add(resultsDirectory);
        arguments.Add("--logger");
        arguments.Add("trx;LogFileName=results.trx");
        if (request.FailedTests.Count > 0)
        {
            var filters = request.FailedTests
                .Select(test => test.FullyQualifiedName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => $"FullyQualifiedName~{name}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (filters.Length > 0)
            {
                arguments.Add("--filter");
                arguments.Add(string.Join('|', filters));
            }
        }

        plan.Commands.Add(new TestPlanCommand
        {
            Name = "Run .NET tests",
            Request = new ProcessRequest
            {
                FileName = ResolveDotNet(),
                Arguments = arguments,
                WorkingDirectory = ResolveWorkingDirectory(
                    request.Project,
                    request.Configuration.Build.WorkingDirectory),
            },
            Format = TestResultFormat.TrxXml,
            ResultPath = Path.Combine(resultsDirectory, "*.trx"),
        });
        plan.SupportsRerun = true;
    }

    private static void BuildCustom(TestPlanRequest request, TestPlan plan)
    {
        var custom = request.Custom;
        if (custom is null || string.IsNullOrWhiteSpace(custom.Command))
        {
            plan.Errors.Add("A custom test command is required.");
        }

        if (custom is null || string.IsNullOrWhiteSpace(custom.ResultGlob))
        {
            plan.Errors.Add("A result glob is required so test results can be read.");
        }

        if (custom is null || plan.Errors.Count > 0)
        {
            return;
        }

        var failedTests = request.FailedTests
            .Select(test => test.FullyQualifiedName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var failedFiles = DistinctFiles(request.FailedTests);
        var failedClasses = request.FailedTests
            .Select(test => test.ClassName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var arguments = new List<string>();
        foreach (var argument in custom.Arguments)
        {
            switch (argument)
            {
                case "${failedTests}":
                    arguments.AddRange(failedTests);
                    break;
                case "${failedFiles}":
                    arguments.AddRange(failedFiles);
                    break;
                case "${failedClasses}":
                    arguments.AddRange(failedClasses);
                    break;
                default:
                    arguments.Add(argument
                        .Replace("${failedTests}", string.Join(' ', failedTests), StringComparison.Ordinal)
                        .Replace("${failedFiles}", string.Join(' ', failedFiles), StringComparison.Ordinal)
                        .Replace("${failedClasses}", string.Join(' ', failedClasses), StringComparison.Ordinal));
                    break;
            }
        }

        plan.Commands.Add(new TestPlanCommand
        {
            Name = "Run custom tests",
            Request = new ProcessRequest
            {
                FileName = custom.Command,
                Arguments = arguments,
                WorkingDirectory = ResolveWorkingDirectory(request.Project, custom.WorkingDirectory),
            },
            Format = TestResultFormat.JUnitXml,
            ResultPath = custom.ResultGlob,
            RequiresDevice = custom.RequiresDevice,
        });
        plan.SupportsRerun = true;
    }

    private static List<string> DistinctFiles(IReadOnlyList<TestTarget> tests) =>
        tests
            .Select(test => test.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> DistinctNames(IReadOnlyList<TestTarget> tests) =>
        tests
            .Select(test => test.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(Regex.Escape)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool IsProjectFile(string? path) =>
        path is not null &&
        (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

    private static string ResolveWorkingDirectory(ProjectModel project, string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory)
            ? project.RootPath
            : Path.IsPathRooted(workingDirectory)
                ? workingDirectory
                : Path.Combine(project.RootPath, workingDirectory);

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

    private static List<string> CreateRerunArguments(
        IReadOnlyList<TestTarget> failedTests,
        bool connected)
    {
        if (failedTests.Count == 0)
        {
            return [];
        }

        if (connected)
        {
            var cases = failedTests
                .Where(test => !string.IsNullOrWhiteSpace(test.Name))
                .Select(test => string.IsNullOrWhiteSpace(test.ClassName)
                    ? test.Name
                    : $"{test.ClassName}#{test.Name}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return cases.Length == 0
                ? []
                : [$"-Pandroid.testInstrumentationRunnerArguments.class={string.Join(',', cases)}"];
        }

        var arguments = new List<string>(failedTests.Count * 2);
        foreach (var test in failedTests)
        {
            if (string.IsNullOrWhiteSpace(test.FullyQualifiedName))
            {
                continue;
            }

            arguments.Add("--tests");
            arguments.Add(test.FullyQualifiedName);
        }

        return arguments;
    }

    private static string Capitalize(string value) =>
        value.Length == 0
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
}
