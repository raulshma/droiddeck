using DroidDeck.Core.Projects;
using DroidDeck.Core.Testing;
using DroidDeck.Projects;

namespace DroidDeck.Projects.Tests;

public sealed class TestPlanBuilderTests
{
    [Fact]
    public void GradleUnit_BuildsTheVariantTaskAndRerunFilters()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateGradleProject();
        var configuration = TemporaryProject.CreateGradleConfiguration();
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.GradleUnit,
            ResultDirectory = directory.Path,
            FailedTests =
            [
                new TestTarget
                {
                    ClassName = "com.example.LoginTests",
                    Name = "rejectsBadPassword",
                },
                new TestTarget
                {
                    ClassName = "com.example.LoginTests",
                    Name = "rejectsExpiredToken",
                },
            ],
        });

        Assert.Empty(plan.Errors);
        var command = Assert.Single(plan.Commands);
        Assert.Equal(TestResultFormat.JUnitXml, command.Format);
        Assert.False(command.RequiresDevice);
        Assert.True(plan.SupportsRerun);
        Assert.Contains(":app:testDebugUnitTest", command.Request.Arguments, StringComparer.Ordinal);
        AssertRerunFilter(command, "com.example.LoginTests.rejectsBadPassword");
        AssertRerunFilter(command, "com.example.LoginTests.rejectsExpiredToken");
        Assert.Equal("**/build/test-results/testDebugUnitTest/*.xml", command.ResultPath);
        Assert.Equal(Path.GetFullPath(directory.Path), command.Request.WorkingDirectory);
    }

    [Fact]
    public void GradleConnected_TargetsTheDeviceAndFiltersFailedCasesByClass()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateGradleProject();
        var configuration = TemporaryProject.CreateGradleConfiguration();
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.GradleConnected,
            DeviceSerial = "emulator-5554",
            ResultDirectory = directory.Path,
            FailedTests =
            [
                new TestTarget
                {
                    ClassName = "com.example.LoginTests",
                    Name = "rejectsBadPassword",
                },
            ],
        });

        Assert.Empty(plan.Errors);
        var command = Assert.Single(plan.Commands);
        Assert.True(command.RequiresDevice);
        Assert.Contains(":app:connectedDebugAndroidTest", command.Request.Arguments, StringComparer.Ordinal);
        Assert.Contains(
            "-Pandroid.testInstrumentationRunnerArguments.class=com.example.LoginTests#rejectsBadPassword",
            command.Request.Arguments,
            StringComparer.Ordinal);
        Assert.Equal("emulator-5554", command.Request.Environment["ANDROID_SERIAL"]);
        Assert.Equal("**/build/outputs/androidTest-results/**/*.xml", command.ResultPath);
    }

    [Fact]
    public void GradleConnected_RequiresADevice()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateGradleProject();
        var configuration = TemporaryProject.CreateGradleConfiguration();
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.GradleConnected,
            ResultDirectory = directory.Path,
        });

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Errors, error => error.Contains("Select a device", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Flutter_BuildsTheJsonReporterAndRerunsByFile()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateFlutterProject();
        var configuration = new RunConfiguration { Kind = RunConfigurationKind.Flutter, VariantName = "debug" };
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.Flutter,
            FailedTests =
            [
                new TestTarget { Name = "login rejects bad password", FilePath = "test/login_test.dart" },
            ],
        });

        Assert.Empty(plan.Errors);
        var command = Assert.Single(plan.Commands);
        Assert.Equal(TestResultFormat.FlutterJson, command.Format);
        Assert.True(command.ReadFromStandardOutput);
        Assert.False(command.RequiresDevice);
        Assert.True(plan.SupportsRerun);
        Assert.Equal(
            ["test", "--reporter", "json", "test/login_test.dart"],
            command.Request.Arguments);
        Assert.Equal(Path.GetFullPath(directory.Path), command.Request.WorkingDirectory);
    }

    [Fact]
    public void Flutter_RerunsByNameWhenTheFileIsUnknown()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateFlutterProject();
        var configuration = new RunConfiguration { Kind = RunConfigurationKind.Flutter, VariantName = "debug" };
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.Flutter,
            FailedTests = [new TestTarget { Name = "login rejects bad password" }],
        });

        var command = Assert.Single(plan.Commands);
        Assert.Contains(
            command.Request.Arguments,
            argument => argument.StartsWith("--name=", StringComparison.Ordinal));
    }

    [Fact]
    public void Jest_WritesJsonResultsAndRerunsByPath()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateReactNativeProject();
        var configuration = new RunConfiguration
        {
            Kind = RunConfigurationKind.ReactNative,
            VariantName = "debug",
        };
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.Jest,
            ResultDirectory = directory.Path,
            FailedTests = [new TestTarget { Name = "renders", FilePath = "__tests__/login.test.tsx" }],
        });

        Assert.Empty(plan.Errors);
        var command = Assert.Single(plan.Commands);
        Assert.Equal(TestResultFormat.JestJson, command.Format);
        Assert.False(command.RequiresDevice);
        var outputFile = Path.Combine(directory.Path, "jest-results.json");
        Assert.Contains("--outputFile=" + outputFile, command.Request.Arguments);
        Assert.Equal(outputFile, command.ResultPath);
        Assert.Contains("--runTestsByPath", command.Request.Arguments);
        Assert.Contains("__tests__/login.test.tsx", command.Request.Arguments);
        Assert.Equal(Path.GetFullPath(directory.Path), command.Request.WorkingDirectory);
    }

    [Fact]
    public void DotNet_WritesTrxResultsAndFiltersFailedTests()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateMauiProject();
        var configuration = new RunConfiguration
        {
            Kind = RunConfigurationKind.Maui,
            Build = new BuildConfiguration
            {
                Task = "Sample.csproj",
                TargetFramework = "net10.0-android",
            },
        };
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.DotNet,
            ResultDirectory = directory.Path,
            FailedTests = [new TestTarget { ClassName = "com.example.LoginTests", Name = "rejects" }],
        });

        Assert.Empty(plan.Errors);
        var command = Assert.Single(plan.Commands);
        Assert.Equal("dotnet", command.Request.FileName);
        Assert.Contains("test", command.Request.Arguments);
        Assert.Contains("Sample.csproj", command.Request.Arguments);
        Assert.Contains("net10.0-android", command.Request.Arguments);
        Assert.Contains("--filter", command.Request.Arguments);
        Assert.Contains(
            "FullyQualifiedName~com.example.LoginTests.rejects",
            command.Request.Arguments);
        var resultsDirectory = Path.Combine(directory.Path, "trx");
        Assert.Contains("--results-directory", command.Request.Arguments);
        Assert.Contains(resultsDirectory, command.Request.Arguments);
        Assert.Contains("trx;LogFileName=results.trx", command.Request.Arguments);
        Assert.Equal(Path.Combine(resultsDirectory, "*.trx"), command.ResultPath);
        Assert.Equal(TestResultFormat.TrxXml, command.Format);
    }

    [Fact]
    public void Custom_SplicesFailedTestsIntoSeparateArguments()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateCustomProject();
        var configuration = new RunConfiguration { Kind = RunConfigurationKind.Custom, Name = "Tests" };
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.Custom,
            Custom = new CustomTestSettings
            {
                Command = "npm",
                Arguments = ["run", "test", "--", "--only", "${failedTests}"],
                WorkingDirectory = "tools",
                ResultGlob = "reports/junit.xml",
            },
            FailedTests =
            [
                new TestTarget { ClassName = "LoginTests", Name = "rejects" },
                new TestTarget { ClassName = "LoginTests", Name = "accepts" },
            ],
        });

        Assert.Empty(plan.Errors);
        var command = Assert.Single(plan.Commands);
        Assert.Equal("npm", command.Request.FileName);
        Assert.Equal(
            ["run", "test", "--", "--only", "LoginTests.rejects", "LoginTests.accepts"],
            command.Request.Arguments);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(directory.Path), "tools"),
            command.Request.WorkingDirectory);
        Assert.Equal("reports/junit.xml", command.ResultPath);
        Assert.Equal(TestResultFormat.JUnitXml, command.Format);
        Assert.True(plan.SupportsRerun);
    }

    [Fact]
    public void Custom_RequiresACommandAndResultGlob()
    {
        using var directory = new TemporaryProject();
        var project = directory.CreateCustomProject();
        var configuration = new RunConfiguration { Kind = RunConfigurationKind.Custom, Name = "Tests" };
        var builder = new TestPlanBuilder(new RunPlanBuilder());

        var plan = builder.Build(new TestPlanRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = TestRunnerKind.Custom,
            Custom = new CustomTestSettings(),
        });

        Assert.Empty(plan.Commands);
        Assert.Contains(plan.Errors, error => error.Contains("command", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Errors, error => error.Contains("result", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRerunFilter(TestPlanCommand command, string expected)
    {
        var index = command.Request.Arguments
            .Select((argument, position) => (argument, position))
            .FirstOrDefault(entry => entry.argument == expected)
            .position;
        Assert.True(index > 0);
        Assert.Equal("--tests", command.Request.Arguments[index - 1]);
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "droiddeck-test-plan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ProjectModel CreateGradleProject()
        {
            File.WriteAllText(
                System.IO.Path.Combine(Path, "settings.gradle.kts"),
                "include(\":app\")");
            var projects = new ProjectModel
            {
                Id = "sample",
                Name = "Sample",
                RootPath = Path,
                Kind = ProjectKind.AndroidGradle,
                ApplicationModule = "app",
                Modules =
                [
                    new ProjectModule
                    {
                        Name = "app",
                        Path = "app",
                        Type = ProjectModuleType.Application,
                    },
                ],
            };
            projects.Variants.Add(new ProjectVariant
            {
                Name = "debug",
                ModuleName = "app",
                BuildType = "debug",
            });
            return projects;
        }

        public static RunConfiguration CreateGradleConfiguration() => new()
        {
            Id = "debug",
            Name = "Debug",
            Kind = RunConfigurationKind.Gradle,
            ModuleName = "app",
            VariantName = "debug",
            Build = new BuildConfiguration { Task = "assembleDebug" },
        };

        public ProjectModel CreateFlutterProject()
        {
            File.WriteAllText(
                System.IO.Path.Combine(Path, "pubspec.yaml"),
                "name: sample\nenvironment:\n  sdk: '>=3.0.0 <4.0.0'\n");
            return new ProjectModel
            {
                Id = "sample",
                Name = "Sample",
                RootPath = Path,
                Kind = ProjectKind.Flutter,
            };
        }

        public ProjectModel CreateReactNativeProject()
        {
            File.WriteAllText(
                System.IO.Path.Combine(Path, "package.json"),
                """{"name":"sample","dependencies":{"react-native":"0.76.0"}}""");
            return new ProjectModel
            {
                Id = "sample",
                Name = "Sample",
                RootPath = Path,
                Kind = ProjectKind.ReactNative,
                ReactNativeInfo = new ReactNativeProjectInfo(),
            };
        }

        public ProjectModel CreateMauiProject() => new()
        {
            Id = "sample",
            Name = "Sample",
            RootPath = Path,
            Kind = ProjectKind.Maui,
            MauiInfo = new MauiProjectInfo
            {
                ProjectFile = "Sample.csproj",
                TargetFrameworks = ["net10.0-android"],
                AndroidTargetFrameworks = ["net10.0-android"],
            },
        };

        public ProjectModel CreateCustomProject() => new()
        {
            Id = "sample",
            Name = "Sample",
            RootPath = Path,
            Kind = ProjectKind.Custom,
        };

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
