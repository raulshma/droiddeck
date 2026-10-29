using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Testing;
using DroidDeck.Plugin.Abstractions;
using TestPluginRegistry = DroidDeck.TestSupport.TestPluginRegistry;

namespace DroidDeck.Core.Tests.Testing;

public sealed class TestRunServiceTests
{
    private const string FlutterOutput = """
        {"test":{"id":1,"name":"works","url":"file:///C:/p/test/a_test.dart","line":1},"type":"testStart","time":0}
        {"testID":1,"result":"success","hidden":false,"skipped":false,"type":"testDone","time":10}
        """;

    private const string JUnitOutput = """
        <testsuite name="LoginTests" tests="1">
          <testcase name="logsIn" classname="com.example.LoginTests" time="0.5" />
        </testsuite>
        """;

    [Fact]
    public async Task RunAsync_ParsesStandardOutputAndForwardsFailedTargets()
    {
        using var directory = new TempDirectory();
        var planBuilder = new FakePlanBuilder
        {
            Handler = _ => new TestPlan
            {
                Runner = TestRunnerKind.Flutter,
                SupportsRerun = true,
                Commands =
                {
                    new TestPlanCommand
                    {
                        Name = "Run Flutter tests",
                        Request = ProcessRequest.Create("flutter", "test"),
                        Format = TestResultFormat.FlutterJson,
                        ReadFromStandardOutput = true,
                    },
                },
            },
        };
        var processes = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult
            {
                CommandLine = "flutter test",
                ExitCode = 0,
                StandardOutput = FlutterOutput,
            },
        };
        var service = new TestRunService(planBuilder, processes);
        var target = new TestTarget { Name = "works", FilePath = "test/a_test.dart" };

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Flutter,
            FailedTests = [target],
        });

        Assert.True(summary.IsSuccess);
        var device = Assert.Single(summary.Devices);
        Assert.Null(device.DeviceSerial);
        Assert.Equal(TestRunOutcome.Passed, device.Outcome);
        Assert.Equal(1, device.PassedCount);
        var suite = Assert.Single(device.Suites);
        Assert.Equal("a_test.dart", suite.Name);
        Assert.Contains("flutter test", device.Output, StringComparison.Ordinal);

        var planRequest = Assert.Single(planBuilder.Requests);
        Assert.Same(target, Assert.Single(planRequest.FailedTests));
    }

    [Fact]
    public async Task RunAsync_RunsEachDeviceAndParsesItsResultFile()
    {
        using var directory = new TempDirectory();
        var planBuilder = new FakePlanBuilder
        {
            Handler = request => new TestPlan
            {
                Runner = TestRunnerKind.GradleConnected,
                Commands =
                {
                    new TestPlanCommand
                    {
                        Name = "Run instrumentation tests",
                        Request = ProcessRequest.Create(
                            "gradle",
                            Path.Combine(request.ResultDirectory!, "results.xml")),
                        Format = TestResultFormat.JUnitXml,
                        ResultPath = Path.Combine(request.ResultDirectory!, "results.xml"),
                        RequiresDevice = true,
                    },
                },
            },
        };
        var processes = new FakeProcessRunner();
        processes.Handler = request =>
        {
            File.WriteAllText(request.Arguments[0], JUnitOutput);
            return new ProcessResult { CommandLine = request.ToCommandLine(), ExitCode = 0 };
        };
        var service = new TestRunService(planBuilder, processes);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.GradleConnected,
            DeviceSerials = ["emulator-5554", "emulator-5556"],
        });

        Assert.True(summary.IsSuccess);
        Assert.Equal(2, summary.Devices.Count);
        Assert.Equal(
            ["emulator-5554", "emulator-5556"],
            summary.Devices.Select(device => device.DeviceSerial).Order(StringComparer.Ordinal));
        Assert.All(summary.Devices, device =>
        {
            Assert.Equal(TestRunOutcome.Passed, device.Outcome);
            Assert.Equal(1, device.PassedCount);
        });
        var requests = planBuilder.Requests.OrderBy(request => request.DeviceSerial, StringComparer.Ordinal).ToArray();
        Assert.Equal(["emulator-5554", "emulator-5556"], requests.Select(request => request.DeviceSerial));
        Assert.NotEqual(requests[0].ResultDirectory, requests[1].ResultDirectory);
    }

    [Fact]
    public async Task RunAsync_ReportsMissingResultsWithThePath()
    {
        using var directory = new TempDirectory();
        var resultPath = Path.Combine(directory.Path, "results.xml");
        var planBuilder = new FakePlanBuilder
        {
            Handler = _ => new TestPlan
            {
                Runner = TestRunnerKind.Custom,
                Commands =
                {
                    new TestPlanCommand
                    {
                        Name = "Run custom tests",
                        Request = ProcessRequest.Create("run-tests"),
                        Format = TestResultFormat.JUnitXml,
                        ResultPath = resultPath,
                    },
                },
            },
        };
        var processes = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult { CommandLine = "run-tests", ExitCode = 1 },
        };
        var service = new TestRunService(planBuilder, processes);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Custom,
        });

        Assert.False(summary.IsSuccess);
        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Empty(device.Suites);
        var error = Assert.Single(device.Errors);
        Assert.Contains("No test result files", error, StringComparison.Ordinal);
        Assert.Contains(resultPath, error, StringComparison.Ordinal);
        Assert.Contains("exited with code 1", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportsMalformedResultsWithTheFile()
    {
        using var directory = new TempDirectory();
        var resultPath = Path.Combine(directory.Path, "results.xml");
        var planBuilder = new FakePlanBuilder
        {
            Handler = _ => new TestPlan
            {
                Runner = TestRunnerKind.Custom,
                Commands =
                {
                    new TestPlanCommand
                    {
                        Request = ProcessRequest.Create("run-tests"),
                        Format = TestResultFormat.JUnitXml,
                        ResultPath = resultPath,
                    },
                },
            },
        };
        var processes = new FakeProcessRunner();
        processes.Handler = _ =>
        {
            File.WriteAllText(resultPath, "<testsuite><testcase></testsuite>");
            return new ProcessResult { CommandLine = "run-tests", ExitCode = 1 };
        };
        var service = new TestRunService(planBuilder, processes);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Custom,
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        var error = Assert.Single(device.Errors);
        Assert.Contains(resultPath, error, StringComparison.Ordinal);
        Assert.Contains("JUnit XML", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsPlanErrorsWithoutRunningAnything()
    {
        using var directory = new TempDirectory();
        var planBuilder = new FakePlanBuilder
        {
            Handler = _ => new TestPlan
            {
                Runner = TestRunnerKind.Custom,
                Errors = { "Select a device before running instrumentation tests." },
            },
        };
        var processes = new FakeProcessRunner();
        var service = new TestRunService(planBuilder, processes);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Custom,
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Contains(
            device.Errors,
            error => error.Contains("Select a device", StringComparison.Ordinal));
        Assert.Empty(processes.Requests);
    }

    [Fact]
    public async Task RunAsync_IgnoresResultFilesLeftFromAnEarlierRun()
    {
        using var directory = new TempDirectory();
        var stale = Path.Combine(directory.Path, "build", "results.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, JUnitOutput);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        var planBuilder = new FakePlanBuilder
        {
            Handler = _ => new TestPlan
            {
                Runner = TestRunnerKind.GradleUnit,
                Commands =
                {
                    new TestPlanCommand
                    {
                        Request = ProcessRequest.Create("gradle", "test"),
                        Format = TestResultFormat.JUnitXml,
                        ResultPath = "**/build/*.xml",
                    },
                },
            },
        };
        var processes = new FakeProcessRunner
        {
            Handler = _ => new ProcessResult { CommandLine = "gradle test", ExitCode = 0 },
        };
        var service = new TestRunService(planBuilder, processes);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.GradleUnit,
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Contains("No test result files", Assert.Single(device.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PluginRunnerKindWithoutAnIdFailsWithGuidance()
    {
        var service = new TestRunService(new FakePlanBuilder(), new FakeProcessRunner());

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(Path.GetTempPath()),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Contains(
            "registered test runner",
            Assert.Single(device.Errors),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ExecutesRegisteredPluginRunnerAndMapsResults()
    {
        using var directory = new TempDirectory();
        var registry = new TestPluginRegistry();
        var runner = new StubTestRunner("plugin.runner")
        {
            Result = new PluginTestRunResult(
                true,
                [
                    new PluginTestSuite("Login", [
                        new PluginTestCase("logsIn", PluginTestStatus.Passed),
                        new PluginTestCase(
                            "logsOut",
                            PluginTestStatus.Failed,
                            ClassName: "LoginTests",
                            Message: "assertion failed"),
                    ]),
                ],
                Output: "plugin output"),
        };
        registry.AddTestRunner(runner);
        var planBuilder = new FakePlanBuilder();
        var service = new TestRunService(planBuilder, new FakeProcessRunner(), plugins: registry);
        var target = new TestTarget { ClassName = "LoginTests", Name = "logsIn" };

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
            PluginRunnerId = "plugin.runner",
            FailedTests = [target],
        });

        Assert.True(summary.IsSuccess);
        Assert.Equal("plugin.runner", summary.PluginRunnerId);
        var device = Assert.Single(summary.Devices);
        Assert.Null(device.DeviceSerial);
        Assert.Equal(TestRunOutcome.Passed, device.Outcome);
        Assert.Equal(2, device.TotalCount);
        Assert.Equal(1, device.FailedCount);
        Assert.Contains("plugin output", device.Output, StringComparison.Ordinal);
        Assert.Empty(planBuilder.Requests);

        var context = Assert.Single(runner.Contexts);
        Assert.Equal(directory.Path, context.ProjectRoot);
        Assert.Equal("Sample", context.ProjectName);
        Assert.Equal("debug", context.VariantName);
        Assert.Null(context.DeviceSerial);
        var failed = Assert.Single(context.FailedTests);
        Assert.Equal("LoginTests", failed.ClassName);
        Assert.Equal("logsIn", failed.Name);
    }

    [Fact]
    public async Task RunAsync_UnregisteredPluginRunnerFailsWithGuidance()
    {
        using var directory = new TempDirectory();
        var service = new TestRunService(
            new FakePlanBuilder(),
            new FakeProcessRunner(),
            plugins: new TestPluginRegistry());

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
            PluginRunnerId = "plugin.missing",
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        var error = Assert.Single(device.Errors);
        Assert.Contains("plugin.missing", error, StringComparison.Ordinal);
        Assert.Contains("not registered", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PluginRunnerThatNeedsADeviceRequiresOne()
    {
        using var directory = new TempDirectory();
        var registry = new TestPluginRegistry();
        var runner = new StubTestRunner("plugin.device") { RequiresDevice = true };
        registry.AddTestRunner(runner);
        var service = new TestRunService(new FakePlanBuilder(), new FakeProcessRunner(), plugins: registry);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
            PluginRunnerId = "plugin.device",
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Contains("device", Assert.Single(device.Errors), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Contexts);
    }

    [Fact]
    public async Task RunAsync_RunsPluginRunnerOncePerDevice()
    {
        using var directory = new TempDirectory();
        var registry = new TestPluginRegistry();
        var runner = new StubTestRunner("plugin.device")
        {
            RequiresDevice = true,
            Result = new PluginTestRunResult(true, [new PluginTestSuite("Suite", [new PluginTestCase("t", PluginTestStatus.Passed)])]),
        };
        registry.AddTestRunner(runner);
        var service = new TestRunService(new FakePlanBuilder(), new FakeProcessRunner(), plugins: registry);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
            PluginRunnerId = "plugin.device",
            DeviceSerials = ["emulator-5554", "emulator-5556"],
        });

        Assert.True(summary.IsSuccess);
        Assert.Equal(2, summary.Devices.Count);
        string?[] expectedSerials = ["emulator-5554", "emulator-5556"];
        Assert.Equal(
            expectedSerials,
            runner.Contexts.Select(context => context.DeviceSerial).OrderBy(serial => serial).ToArray());
    }

    [Fact]
    public async Task RunAsync_PluginRunnerFailureIsReported()
    {
        using var directory = new TempDirectory();
        var registry = new TestPluginRegistry();
        registry.AddTestRunner(new StubTestRunner("plugin.runner")
        {
            Result = new PluginTestRunResult(false, Error: "the toolchain is missing"),
        });
        var service = new TestRunService(new FakePlanBuilder(), new FakeProcessRunner(), plugins: registry);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
            PluginRunnerId = "plugin.runner",
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Contains("the toolchain is missing", Assert.Single(device.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PluginRunnerExceptionIsIsolated()
    {
        using var directory = new TempDirectory();
        var registry = new TestPluginRegistry();
        registry.AddTestRunner(new StubTestRunner("plugin.runner")
        {
            Throw = new InvalidOperationException("kaboom"),
        });
        var service = new TestRunService(new FakePlanBuilder(), new FakeProcessRunner(), plugins: registry);

        var summary = await service.RunAsync(new TestRunRequest
        {
            Project = CreateProject(directory.Path),
            Configuration = CreateConfiguration(),
            Runner = TestRunnerKind.Plugin,
            PluginRunnerId = "plugin.runner",
        });

        var device = Assert.Single(summary.Devices);
        Assert.Equal(TestRunOutcome.Failed, device.Outcome);
        Assert.Contains("kaboom", Assert.Single(device.Errors), StringComparison.Ordinal);
    }

    private static ProjectModel CreateProject(string root) => new()
    {
        Id = "sample",
        Name = "Sample",
        RootPath = root,
        Kind = ProjectKind.Flutter,
    };

    private static RunConfiguration CreateConfiguration() => new()
    {
        Id = "debug",
        Name = "Debug",
        Kind = RunConfigurationKind.Flutter,
        VariantName = "debug",
    };

    private sealed class FakePlanBuilder : ITestPlanBuilder
    {
        public List<TestPlanRequest> Requests { get; } = [];

        public Func<TestPlanRequest, TestPlan> Handler { get; set; } = _ => new TestPlan();

        public TestPlan Build(TestPlanRequest request)
        {
            Requests.Add(request);
            return Handler(request);
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Func<ProcessRequest, ProcessResult> Handler { get; set; } =
            request => new ProcessResult { CommandLine = request.ToCommandLine() };

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }
    }

    private sealed class StubTestRunner(string id) : ITestRunner
    {
        public string Id => id;

        public string Name => id;

        public string? Description => null;

        public bool RequiresDevice { get; init; }

        public bool SupportsRerun { get; init; }

        public PluginTestRunResult Result { get; init; } = new(true);

        public Exception? Throw { get; init; }

        public List<TestRunnerContext> Contexts { get; } = [];

        public Task<PluginTestRunResult> RunAsync(TestRunnerContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Contexts)
            {
                Contexts.Add(context);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "droiddeck-test-run-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
