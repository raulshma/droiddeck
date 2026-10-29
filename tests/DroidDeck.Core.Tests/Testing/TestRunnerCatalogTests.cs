using DroidDeck.Core.Projects;
using DroidDeck.Core.Testing;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Testing;

public sealed class TestRunnerCatalogTests
{
    [Fact]
    public void GetChoices_ListsTheBuiltInRunnersForGradleProjects()
    {
        var choices = TestRunnerCatalog.GetChoices(ProjectKind.AndroidGradle, []);

        Assert.Equal(
            [TestRunnerKind.GradleUnit, TestRunnerKind.GradleConnected, TestRunnerKind.Custom],
            choices.Select(choice => choice.Kind).ToArray());
        Assert.All(choices, choice => Assert.Null(choice.PluginRunnerId));
    }

    [Fact]
    public void GetChoices_FlutterProjectsDoNotOfferGradle()
    {
        var choices = TestRunnerCatalog.GetChoices(ProjectKind.Flutter, []);

        Assert.Contains(choices, choice => choice.Kind == TestRunnerKind.Flutter);
        Assert.DoesNotContain(
            choices,
            choice => choice.Kind is TestRunnerKind.GradleUnit or TestRunnerKind.GradleConnected);
    }

    [Fact]
    public void GetChoices_AppendsPluginRunnersWithTheirMetadata()
    {
        var registry = new TestPluginRegistry();
        registry.AddTestRunner(new StubRunner("plugin.runner")
        {
            Name = "Playwright tests",
            RequiresDevice = true,
            SupportsRerun = true,
        });

        var choices = TestRunnerCatalog.GetChoices(ProjectKind.Flutter, registry.TestRunners);
        var plugin = choices[^1];

        Assert.Equal(TestRunnerKind.Plugin, plugin.Kind);
        Assert.Equal("Playwright tests", plugin.Label);
        Assert.Equal("plugin.runner", plugin.PluginRunnerId);
        Assert.True(plugin.RequiresDevice);
        Assert.True(plugin.SupportsRerun);
    }

    private sealed class StubRunner : ITestRunner
    {
        public StubRunner(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; }

        public string Name { get; init; }

        public string? Description => null;

        public bool RequiresDevice { get; init; }

        public bool SupportsRerun { get; init; }

        public Task<PluginTestRunResult> RunAsync(TestRunnerContext context, CancellationToken cancellationToken)
            => Task.FromResult(new PluginTestRunResult(true));
    }
}
