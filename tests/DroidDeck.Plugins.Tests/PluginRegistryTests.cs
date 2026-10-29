using DroidDeck.Core;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins.BuiltIns;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginRegistryTests
{
    [Fact]
    public void BuiltInCommandIsRegisteredAndOwnedByBuiltInPlugin()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        var registry = new PluginRegistry();

        BuiltInExtensions.Register(registry, paths);

        var command = Assert.Single(registry.Commands);
        Assert.Equal(PluginConstants.BuiltInPluginId, command.PluginId);
        Assert.IsType<OpenDataDirectoryCommand>(command.Value);
        Assert.Equal(OpenDataDirectoryCommand.CommandId, command.Value.Id);
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.GetOwner(command.Value.GetType()));
    }

    [Fact]
    public void BuiltInAndPluginExtensionsShareOneRegistry()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        var registry = new PluginRegistry();
        BuiltInExtensions.Register(registry, paths);
        var command = new TestCommand("plugin.command");

        registry.CreateScope("com.example.plugin").AddCommand(command);

        Assert.Equal(2, registry.Commands.Count);
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.GetOwner(typeof(OpenDataDirectoryCommand)));
        Assert.Equal("com.example.plugin", registry.GetOwner(command.GetType()));
    }

    [Fact]
    public void ScopedRegistryAttributesEveryExtensionKindToThePlugin()
    {
        var registry = new PluginRegistry();
        var scope = registry.CreateScope("com.example.plugin");
        var command = new TestCommand();
        var detector = new TestDetector();
        var action = new TestDeviceAction();
        var step = new TestRunStep();
        var parser = new TestLogParser();
        var provider = new TestHighlightRuleProvider();
        var testRunner = new TestRunner();

        scope.AddCommand(command);
        scope.AddProjectDetector(detector);
        scope.AddDeviceAction(action);
        scope.AddRunStep(step);
        scope.AddLogParser(parser);
        scope.AddHighlightRuleProvider(provider);
        scope.AddTestRunner(testRunner);

        Assert.Equal("com.example.plugin", Assert.Single(registry.Commands).PluginId);
        Assert.Equal("com.example.plugin", Assert.Single(registry.ProjectDetectors).PluginId);
        Assert.Equal("com.example.plugin", Assert.Single(registry.DeviceActions).PluginId);
        Assert.Equal("com.example.plugin", Assert.Single(registry.RunSteps).PluginId);
        Assert.Equal("com.example.plugin", Assert.Single(registry.LogParsers).PluginId);
        Assert.Equal("com.example.plugin", Assert.Single(registry.HighlightRuleProviders).PluginId);
        Assert.Equal("com.example.plugin", Assert.Single(registry.TestRunners).PluginId);
        Assert.Equal("com.example.plugin", registry.GetOwner(command.GetType()));
        Assert.Equal("com.example.plugin", registry.GetOwner(detector.GetType()));
        Assert.Equal("com.example.plugin", registry.GetOwner(action.GetType()));
        Assert.Equal("com.example.plugin", registry.GetOwner(step.GetType()));
        Assert.Equal("com.example.plugin", registry.GetOwner(parser.GetType()));
        Assert.Equal("com.example.plugin", registry.GetOwner(provider.GetType()));
        Assert.Equal("com.example.plugin", registry.GetOwner(testRunner.GetType()));
    }

    [Fact]
    public void RemoveAllRemovesOnlyTheTargetPluginAndRaisesChanged()
    {
        using var temp = new TempDirectory();
        var registry = new PluginRegistry();
        BuiltInExtensions.Register(registry, new AppPaths(temp.Combine("data")));
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        var pluginCommand = new TestCommand("plugin.command");
        registry.CreateScope("com.example.plugin").AddCommand(pluginCommand);
        changes = 0;

        var removed = registry.RemoveAll("com.example.plugin");

        Assert.Equal(1, removed);
        Assert.Equal(1, changes);
        Assert.Equal(OpenDataDirectoryCommand.CommandId, Assert.Single(registry.Commands).Value.Id);
        Assert.Null(registry.GetOwner(pluginCommand.GetType()));
        Assert.Equal(PluginConstants.BuiltInPluginId, registry.GetOwner(typeof(OpenDataDirectoryCommand)));
    }

    [Fact]
    public void RemoveAllForUnknownPluginDoesNothing()
    {
        var registry = new PluginRegistry();
        var changes = 0;
        registry.Changed += (_, _) => changes++;
        registry.AddCommand(new TestCommand());
        changes = 0;

        var removed = registry.RemoveAll("com.example.absent");

        Assert.Equal(0, removed);
        Assert.Equal(0, changes);
        Assert.Single(registry.Commands);
    }

    [Fact]
    public void CommandSnapshotDoesNotChangeAfterLaterRegistrations()
    {
        var registry = new PluginRegistry();
        registry.AddCommand(new TestCommand("first"));

        var snapshot = registry.Commands;
        registry.AddCommand(new TestCommand("second"));

        Assert.Single(snapshot);
        Assert.Equal(2, registry.Commands.Count);
    }

    [Fact]
    public async Task RegisteredCommandExecutesThroughTheRegistry()
    {
        var registry = new PluginRegistry();
        var command = new TestCommand();
        registry.CreateScope("com.example.plugin").AddCommand(command);

        await registry.Commands[0].Value.ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, command.ExecuteCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateScopeRejectsBlankPluginIds(string? pluginId)
    {
        var registry = new PluginRegistry();

        Assert.ThrowsAny<ArgumentException>(() => registry.CreateScope(pluginId!));
    }

    [Fact]
    public void AddCommandRejectsNull()
    {
        var registry = new PluginRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.AddCommand(null!));
    }

    private sealed class TestCommand(string id = "test.command") : ICommand
    {
        public int ExecuteCount { get; private set; }

        public string Id => id;

        public string Title => id;

        public string Description => id;

        public IReadOnlyList<string> Keywords => [];

        public Task ExecuteAsync(CancellationToken cancellationToken)
        {
            ExecuteCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestDetector : IProjectDetector
    {
        public string Id => "test.detector";

        public int Priority => 0;

        public Task<ProjectDetection?> DetectAsync(
            ProjectDetectionContext context,
            CancellationToken cancellationToken) => Task.FromResult<ProjectDetection?>(null);
    }

    private sealed class TestDeviceAction : IDeviceAction
    {
        public string Id => "test.action";

        public string Name => "Test action";

        public bool RequiresConfirmation => false;

        public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class TestRunStep : IRunStep
    {
        public string Id => "test.step";

        public Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken cancellationToken)
            => Task.FromResult(new RunStepResult(true, "ok"));
    }

    private sealed class TestLogParser : ILogParser
    {
        public string Id => "test.parser";

        public string Name => "Test parser";

        public int Priority => 0;

        public LogParserResult? Parse(LogParserContext context) => null;
    }

    private sealed class TestHighlightRuleProvider : IHighlightRuleProvider
    {
        public string Id => "test.highlights";

        public IReadOnlyList<PluginHighlightRule> Rules =>
        [
            new("test.rule", "Test rule", "needle"),
        ];
    }

    private sealed class TestRunner : ITestRunner
    {
        public string Id => "test.runner";

        public string Name => "Test runner";

        public string? Description => null;

        public bool RequiresDevice => false;

        public bool SupportsRerun => false;

        public Task<PluginTestRunResult> RunAsync(TestRunnerContext context, CancellationToken cancellationToken)
            => Task.FromResult(new PluginTestRunResult(true));
    }
}
