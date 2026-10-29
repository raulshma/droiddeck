using System.Text;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Core.Testing;

/// <summary>Runs test plans and registered plugin runners on the host or several devices in parallel.</summary>
public sealed class TestRunService : ITestRunService
{
    private readonly ITestPlanBuilder _plans;
    private readonly IProcessRunner _processes;
    private readonly IDeviceOperationCoordinator? _coordinator;
    private readonly IPluginRegistry? _plugins;

    /// <summary>Creates a runner that uses the supplied plan builder and process runner.</summary>
    public TestRunService(
        ITestPlanBuilder plans,
        IProcessRunner processes,
        IDeviceOperationCoordinator? coordinator = null,
        IPluginRegistry? plugins = null)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _coordinator = coordinator;
        _plugins = plugins;
    }

    /// <inheritdoc />
    public async Task<TestRunSummary> RunAsync(
        TestRunRequest request,
        IProgress<TestRunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var summary = new TestRunSummary
        {
            Runner = request.Runner,
            PluginRunnerId = request.PluginRunnerId,
        };
        var serials = request.DeviceSerials
            .Where(serial => !string.IsNullOrWhiteSpace(serial))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var runDirectory = Path.Combine(
            Path.GetTempPath(),
            "droiddeck-tests",
            Guid.NewGuid().ToString("N"));
        if (request.Runner == TestRunnerKind.Plugin &&
            string.IsNullOrWhiteSpace(request.PluginRunnerId))
        {
            summary.Devices.Add(new TestRunDeviceResult
            {
                Errors = { "Select a registered test runner before running tests." },
            });
            return summary;
        }

        if (!string.IsNullOrWhiteSpace(request.PluginRunnerId))
        {
            var pluginRunner = ResolvePluginRunner(request.PluginRunnerId);
            if (pluginRunner is null)
            {
                summary.Devices.Add(new TestRunDeviceResult
                {
                    Errors =
                    {
                        $"The test runner '{request.PluginRunnerId}' is not registered. " +
                        "Install the plugin that provides it and restart DroidDeck.",
                    },
                });
                return summary;
            }

            if (pluginRunner.RequiresDevice && serials.Length == 0)
            {
                summary.Devices.Add(new TestRunDeviceResult
                {
                    Errors =
                    {
                        $"The test runner '{pluginRunner.Name}' requires a connected device. " +
                        "Select at least one device.",
                    },
                });
                return summary;
            }

            string?[] pluginTargets = serials.Length == 0
                ? [null]
                : Array.ConvertAll(serials, serial => (string?)serial);
            var pluginTasks = pluginTargets
                .Select(serial => RunPluginDeviceAsync(
                    pluginRunner,
                    request,
                    serial,
                    Path.Combine(runDirectory, serial is null ? "host" : Sanitize(serial)),
                    progress,
                    cancellationToken))
                .ToArray();
            var pluginResults = await Task.WhenAll(pluginTasks).ConfigureAwait(false);
            summary.Devices.AddRange(pluginResults);
            summary.IsCanceled = cancellationToken.IsCancellationRequested ||
                                 pluginResults.Any(result => result.Outcome == TestRunOutcome.Cancelled);
            return summary;
        }

        if (serials.Length == 0)
        {
            var result = await RunDeviceAsync(
                request,
                null,
                Path.Combine(runDirectory, "host"),
                progress,
                cancellationToken).ConfigureAwait(false);
            summary.Devices.Add(result);
            summary.IsCanceled = result.Outcome == TestRunOutcome.Cancelled;
            return summary;
        }

        var tasks = serials
            .Select(serial => RunDeviceAsync(
                request,
                serial,
                Path.Combine(runDirectory, Sanitize(serial)),
                progress,
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        summary.Devices.AddRange(results);
        summary.IsCanceled = cancellationToken.IsCancellationRequested ||
                             results.Any(result => result.Outcome == TestRunOutcome.Cancelled);
        return summary;
    }

    private ITestRunner? ResolvePluginRunner(string pluginRunnerId)
    {
        if (_plugins is null)
        {
            return null;
        }

        return _plugins.TestRunners.FirstOrDefault(extension =>
            string.Equals(extension.Value.Id, pluginRunnerId, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private async Task<TestRunDeviceResult> RunPluginDeviceAsync(
        ITestRunner runner,
        TestRunRequest request,
        string? serial,
        string resultDirectory,
        IProgress<TestRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new TestRunDeviceResult { DeviceSerial = serial };
        Directory.CreateDirectory(resultDirectory);
        progress?.Report(new TestRunProgress
        {
            DeviceSerial = serial,
            Message = $"Running {runner.Name}",
            Kind = TestRunProgressKind.Started,
        });

        PluginTestRunResult runResult;
        IAsyncDisposable? lease = null;
        try
        {
            if (runner.RequiresDevice && serial is not null && _coordinator is not null)
            {
                lease = await _coordinator
                    .AcquireAsync(serial, $"tests: {runner.Name}", cancellationToken)
                    .ConfigureAwait(false);
            }

            runResult = await runner.RunAsync(
                BuildPluginContext(request, serial, resultDirectory),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.Outcome = TestRunOutcome.Cancelled;
            return result;
        }
        catch (Exception exception)
        {
            result.Errors.Add($"The test runner '{runner.Name}' failed: {exception.Message}");
            return result;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        result.Output = runResult.Output ?? string.Empty;
        if (runResult.Suites is not null)
        {
            foreach (var suite in runResult.Suites)
            {
                result.Suites.Add(MapSuite(suite));
            }
        }

        if (!runResult.Succeeded)
        {
            result.Errors.Add(string.IsNullOrWhiteSpace(runResult.Error)
                ? $"The test runner '{runner.Name}' did not produce results."
                : runResult.Error);
        }
        else if (result.Suites.Count == 0)
        {
            result.Errors.Add($"The test runner '{runner.Name}' did not report any test cases.");
        }

        result.Outcome = result.Errors.Count == 0 && result.Suites.Count > 0
            ? TestRunOutcome.Passed
            : TestRunOutcome.Failed;
        progress?.Report(new TestRunProgress
        {
            DeviceSerial = serial,
            Message = $"Finished {runner.Name}",
            Kind = TestRunProgressKind.Completed,
        });
        return result;
    }

    private static TestRunnerContext BuildPluginContext(
        TestRunRequest request,
        string? serial,
        string resultDirectory) => new()
    {
        ProjectRoot = request.Project.RootPath,
        ProjectName = request.Project.Name,
        DeviceSerial = serial,
        VariantName = request.Configuration.VariantName,
        ModuleName = request.Configuration.ModuleName,
        PackageName = request.Configuration.Launch.PackageName ?? request.Configuration.Artifact.PackageName,
        FailedTests = request.FailedTests
            .Select(target => new TestRunnerTarget(target.ClassName, target.Name, target.FilePath))
            .ToArray(),
        ResultDirectory = resultDirectory,
    };

    private static TestSuiteResult MapSuite(PluginTestSuite suite) => new()
    {
        Name = suite.Name,
        Cases = suite.Cases
            .Select(test => new TestCaseResult
            {
                Name = test.Name,
                ClassName = test.ClassName,
                SuiteName = suite.Name,
                Status = MapStatus(test.Status),
                Duration = test.DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                Message = test.Message,
                StackTrace = test.StackTrace,
                FilePath = test.FilePath,
                Line = test.Line,
                Attachments = test.Attachments is null
                    ? []
                    : test.Attachments
                        .Select(attachment => new TestAttachment
                        {
                            Name = attachment.Name,
                            Path = attachment.Path,
                            Content = attachment.Content,
                        })
                        .ToList(),
            })
            .ToList(),
    };

    private static TestStatus MapStatus(PluginTestStatus status) => status switch
    {
        PluginTestStatus.Passed => TestStatus.Passed,
        PluginTestStatus.Failed => TestStatus.Failed,
        PluginTestStatus.Errored => TestStatus.Errored,
        _ => TestStatus.Skipped,
    };

    private async Task<TestRunDeviceResult> RunDeviceAsync(
        TestRunRequest request,
        string? serial,
        string resultDirectory,
        IProgress<TestRunProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new TestRunDeviceResult { DeviceSerial = serial };
        TestPlan plan;
        try
        {
            plan = _plans.Build(new TestPlanRequest
            {
                Project = request.Project,
                Configuration = request.Configuration,
                Runner = request.Runner,
                DeviceSerial = serial,
                FailedTests = request.FailedTests,
                ResultDirectory = resultDirectory,
                Custom = request.Custom,
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result.Errors.Add($"The test plan could not be built: {exception.Message}");
            return result;
        }

        if (plan.Errors.Count > 0)
        {
            result.Errors.AddRange(plan.Errors);
            return result;
        }

        if (plan.Commands.Count == 0)
        {
            result.Errors.Add("The test plan did not contain any commands.");
            return result;
        }

        var output = new StringBuilder();
        var cancelled = false;
        foreach (var command in plan.Commands)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            progress?.Report(new TestRunProgress
            {
                DeviceSerial = serial,
                Message = $"Running {command.Name}",
                Kind = TestRunProgressKind.Started,
            });
            PrepareResultDirectory(request, command, resultDirectory);
            var startedAt = DateTime.UtcNow;
            ProcessResult process;
            IAsyncDisposable? lease = null;
            try
            {
                if (command.RequiresDevice && serial is not null && _coordinator is not null)
                {
                    lease = await _coordinator
                        .AcquireAsync(serial, $"tests: {command.Name}", cancellationToken)
                        .ConfigureAwait(false);
                }

                process = await _processes
                    .RunAsync(command.Request, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            finally
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }

            AppendOutput(output, command, process);
            if (process.WasCanceled)
            {
                cancelled = true;
                break;
            }

            CollectResults(request, command, process, result, startedAt, cancellationToken);
            progress?.Report(new TestRunProgress
            {
                DeviceSerial = serial,
                Message = $"Finished {command.Name}",
                Kind = TestRunProgressKind.Completed,
            });
        }

        result.Output = output.ToString();
        result.Outcome = cancelled
            ? TestRunOutcome.Cancelled
            : result.Errors.Count == 0 && result.Suites.Count > 0
                ? TestRunOutcome.Passed
                : TestRunOutcome.Failed;
        return result;
    }

    private static void CollectResults(
        TestRunRequest request,
        TestPlanCommand command,
        ProcessResult process,
        TestRunDeviceResult result,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        if (command.ReadFromStandardOutput)
        {
            if (string.IsNullOrWhiteSpace(process.StandardOutput))
            {
                result.Errors.Add(
                    $"\"{command.Name}\" did not produce any output. {DescribeProcess(process)}");
                return;
            }

            List<TestSuiteResult> suites;
            try
            {
                suites = [.. TestResultParser.Parse(command.Format, process.StandardOutput)];
            }
            catch (TestResultParseException exception)
            {
                result.Errors.Add($"Could not read the test results: {exception.Message}");
                return;
            }

            if (suites.Sum(suite => suite.Cases.Count) == 0)
            {
                result.Errors.Add($"The output of \"{command.Name}\" did not contain any test cases.");
                return;
            }

            result.Suites.AddRange(suites);
            return;
        }

        if (string.IsNullOrWhiteSpace(command.ResultPath))
        {
            result.Errors.Add($"\"{command.Name}\" does not declare where its test results are written.");
            return;
        }

        List<string> files;
        try
        {
            files = TestResultLocator.Find(
                request.Project.RootPath,
                command.ResultPath,
                startedAt,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.Errors.Add(
                $"The test result folder could not be searched: {exception.Message}");
            return;
        }

        if (files.Count == 0)
        {
            var device = string.IsNullOrWhiteSpace(result.DeviceSerial)
                ? string.Empty
                : $" for {result.DeviceSerial}";
            result.Errors.Add(
                $"No test result files matched \"{command.ResultPath}\"{device}. " +
                DescribeProcess(process));
            return;
        }

        var before = result.Suites.Sum(suite => suite.Cases.Count);
        foreach (var file in files)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result.Errors.Add($"The test result file could not be read: {exception.Message}");
                continue;
            }

            try
            {
                result.Suites.AddRange(TestResultParser.Parse(command.Format, content, file));
            }
            catch (TestResultParseException exception)
            {
                result.Errors.Add($"Could not read the test results: {exception.Message}");
            }
        }

        var after = result.Suites.Sum(suite => suite.Cases.Count);
        if (result.Errors.Count == 0 && after == before)
        {
            result.Errors.Add(
                $"The test results in \"{command.ResultPath}\" did not contain any test cases.");
        }
    }

    private static void PrepareResultDirectory(
        TestRunRequest request,
        TestPlanCommand command,
        string resultDirectory)
    {
        Directory.CreateDirectory(resultDirectory);
        if (string.IsNullOrWhiteSpace(command.ResultPath))
        {
            return;
        }

        var full = Path.IsPathRooted(command.ResultPath)
            ? command.ResultPath
            : Path.Combine(request.Project.RootPath, command.ResultPath);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(directory) && directory.IndexOfAny(['*', '?']) < 0)
        {
            Directory.CreateDirectory(directory);
        }

        if (command.ResultPath.IndexOfAny(['*', '?']) >= 0)
        {
            return;
        }

        try
        {
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AppendOutput(
        StringBuilder output,
        TestPlanCommand command,
        ProcessResult process)
    {
        output.Append("> ").AppendLine(command.Request.ToCommandLine());
        if (!string.IsNullOrWhiteSpace(process.StandardOutput))
        {
            output.AppendLine(process.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(process.StandardError))
        {
            output.AppendLine(process.StandardError.TrimEnd());
        }

        if (process.StartError is not null)
        {
            output.AppendLine(process.StartError);
        }
    }

    private static string DescribeProcess(ProcessResult process) =>
        process.StartError is not null
            ? $"The command could not start: {process.StartError}"
            : process.TimedOut
                ? "The command timed out before writing results."
                : $"The command exited with code {process.ExitCode}.";

    private static string Sanitize(string serial) =>
        string.Concat(serial.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
}
