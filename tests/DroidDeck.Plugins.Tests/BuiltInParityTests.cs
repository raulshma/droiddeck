using DroidDeck.Core;
using DroidDeck.Core.Processes;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins.BuiltIns;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class BuiltInParityTests
{
    [Fact]
    public void BuiltInRunStepUsesTheSameRegistryAsPluginRunSteps()
    {
        using var temp = new TempDirectory();
        var registry = new PluginRegistry();
        var builtInStep = new WaitForDeviceRunStep(new StubRunner(), () => "adb");
        BuiltInExtensions.Register(registry, new AppPaths(temp.Combine("data")), [builtInStep]);

        var pluginStep = new StubStep("com.example.plugin.step");
        registry.CreateScope("com.example.plugin").AddRunStep(pluginStep);

        Assert.Equal(2, registry.RunSteps.Count);
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.RunSteps[0].PluginId);
        Assert.Same(builtInStep, registry.RunSteps[0].Value);
        Assert.Equal("com.example.plugin", registry.RunSteps[1].PluginId);
        Assert.Same(pluginStep, registry.RunSteps[1].Value);
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.GetOwner(typeof(WaitForDeviceRunStep)));
        Assert.Equal("com.example.plugin", registry.GetOwner(pluginStep.GetType()));
    }

    [Fact]
    public void BuiltInLogParsersAndRulesUseTheSameRegistryAsPlugins()
    {
        using var temp = new TempDirectory();
        var registry = new PluginRegistry();
        BuiltInExtensions.Register(registry, new AppPaths(temp.Combine("data")));

        registry.CreateScope("com.example.plugin").AddLogParser(new StubParser());
        registry.CreateScope("com.example.plugin").AddHighlightRuleProvider(new StubRuleProvider());

        Assert.Equal(2, registry.LogParsers.Count);
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.LogParsers[0].PluginId);
        Assert.IsType<AndroidCrashLogParser>(registry.LogParsers[0].Value);
        Assert.Equal("com.example.plugin", registry.LogParsers[1].PluginId);

        Assert.Equal(2, registry.HighlightRuleProviders.Count);
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.HighlightRuleProviders[0].PluginId);
        var builtInRules = registry.HighlightRuleProviders[0].Value.Rules;
        Assert.Contains(builtInRules, rule => rule.Id == "fatal");
        Assert.Contains(builtInRules, rule => rule.Id == "error");
        Assert.Equal("com.example.plugin", registry.HighlightRuleProviders[1].PluginId);
    }

    [Fact]
    public void AndroidCrashParserFlagsFatalAndAnrLines()
    {
        var parser = new AndroidCrashLogParser();

        var fatal = parser.Parse(new LogParserContext(
            "serial",
            "E/AndroidRuntime: FATAL EXCEPTION: main",
            "AndroidRuntime",
            "E",
            "FATAL EXCEPTION: main"));
        var anr = parser.Parse(new LogParserContext(
            "serial",
            "E/ActivityManager: ANR in com.example.app",
            "ActivityManager",
            "E",
            "ANR in com.example.app"));
        var routine = parser.Parse(new LogParserContext("serial", "D/Tag: hello", "Tag", "D", "hello"));

        Assert.NotNull(fatal);
        Assert.Equal("fatal", fatal.HighlightRuleId);
        Assert.Equal("F", fatal.Level);
        Assert.NotNull(anr);
        Assert.Equal("fatal", anr.HighlightRuleId);
        Assert.Null(routine);
    }

    [Fact]
    public void BuiltInRunStepsCarryStableIdentifiers()
    {
        Assert.Equal("droiddeck.builtin.runStep.waitForDevice", WaitForDeviceRunStep.StepId);
        Assert.Equal(WaitForDeviceRunStep.StepId, new WaitForDeviceRunStep(new StubRunner(), () => "adb").Id);
    }

    [Fact]
    public async Task WaitForDeviceRunsAdbAgainstTheTargetSerial()
    {
        var runner = new StubRunner();
        var step = new WaitForDeviceRunStep(runner, () => @"C:\sdk\adb.exe");

        var result = await step.ExecuteAsync(new RunStepContext(@"C:\project", "emulator-5554"), default);

        Assert.True(result.Succeeded);
        Assert.Equal("Device emulator-5554 is ready.", result.Summary);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(@"C:\sdk\adb.exe", request.FileName);
        Assert.Equal(["-s", "emulator-5554", "wait-for-device"], request.Arguments);
    }

    [Fact]
    public async Task WaitForDeviceFailsWithoutADevice()
    {
        var runner = new StubRunner();
        var step = new WaitForDeviceRunStep(runner, () => "adb");

        var result = await step.ExecuteAsync(new RunStepContext(@"C:\project", null), default);

        Assert.False(result.Succeeded);
        Assert.Contains("device", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task WaitForDeviceReportsTimeout()
    {
        var runner = new StubRunner
        {
            Result = new ProcessResult { CommandLine = "adb", TimedOut = true },
        };
        var step = new WaitForDeviceRunStep(runner, () => "adb");

        var result = await step.ExecuteAsync(new RunStepContext(@"C:\project", "serial-1"), default);

        Assert.False(result.Succeeded);
        Assert.Contains("did not become ready", result.Summary);
    }

    [Fact]
    public async Task WaitForDeviceSurfacesStartErrors()
    {
        var runner = new StubRunner
        {
            Result = new ProcessResult { CommandLine = "adb", StartError = "adb was not found." },
        };
        var step = new WaitForDeviceRunStep(runner, () => "adb");

        var result = await step.ExecuteAsync(new RunStepContext(@"C:\project", "serial-1"), default);

        Assert.False(result.Succeeded);
        Assert.Equal("adb was not found.", result.Summary);
    }

    private sealed class StubRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public ProcessResult? Result { get; init; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Result ?? new ProcessResult { CommandLine = request.ToCommandLine(), ExitCode = 0 });
        }
    }

    private sealed class StubStep(string id) : IRunStep
    {
        public string Id => id;

        public Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken cancellationToken)
            => Task.FromResult(new RunStepResult(true, "ok"));
    }

    private sealed class StubParser : ILogParser
    {
        public string Id => "com.example.plugin.parser";

        public string Name => "Plugin parser";

        public int Priority => 0;

        public LogParserResult? Parse(LogParserContext context) => null;
    }

    private sealed class StubRuleProvider : IHighlightRuleProvider
    {
        public string Id => "com.example.plugin.rules";

        public IReadOnlyList<PluginHighlightRule> Rules => [new("plugin.rule", "Plugin rule", "needle")];
    }
}
