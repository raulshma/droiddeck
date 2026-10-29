using DroidDeck.Core.Projects;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Core.Testing;

/// <summary>One runner offered in the test UI.</summary>
/// <param name="Kind">The built-in runner kind, or <see cref="TestRunnerKind.Plugin"/>.</param>
/// <param name="Label">The picker label.</param>
/// <param name="PluginRunnerId">The registered runner identifier for plugin runners.</param>
/// <param name="RequiresDevice">Whether the runner needs a connected device.</param>
/// <param name="SupportsRerun">Whether a failed-only rerun can narrow the selection.</param>
public sealed record TestRunnerChoice(
    TestRunnerKind Kind,
    string Label,
    string? PluginRunnerId = null,
    bool RequiresDevice = false,
    bool SupportsRerun = false);

/// <summary>Builds the runner list for a project from built-ins and the plugin registry.</summary>
public static class TestRunnerCatalog
{
    /// <summary>Gets the built-in runners for a project kind followed by registered plugin runners.</summary>
    public static IReadOnlyList<TestRunnerChoice> GetChoices(
        ProjectKind projectKind,
        IEnumerable<PluginExtension<ITestRunner>> pluginRunners)
    {
        ArgumentNullException.ThrowIfNull(pluginRunners);
        var choices = new List<TestRunnerChoice>();
        switch (projectKind)
        {
            case ProjectKind.AndroidGradle or ProjectKind.KotlinMultiplatform
                or ProjectKind.Capacitor or ProjectKind.Cordova:
                choices.Add(new(TestRunnerKind.GradleUnit, "Gradle unit tests"));
                choices.Add(new(TestRunnerKind.GradleConnected, "Gradle connected tests"));
                choices.Add(new(TestRunnerKind.Custom, "Custom command"));
                break;
            case ProjectKind.Flutter:
                choices.Add(new(TestRunnerKind.Flutter, "Flutter tests"));
                choices.Add(new(TestRunnerKind.Custom, "Custom command"));
                break;
            case ProjectKind.ReactNative:
                choices.Add(new(TestRunnerKind.Jest, "Jest tests"));
                choices.Add(new(TestRunnerKind.Custom, "Custom command"));
                break;
            case ProjectKind.Maui:
                choices.Add(new(TestRunnerKind.DotNet, ".NET tests"));
                choices.Add(new(TestRunnerKind.Custom, "Custom command"));
                break;
            default:
                choices.Add(new(TestRunnerKind.Custom, "Custom command"));
                break;
        }

        foreach (var runner in pluginRunners)
        {
            choices.Add(new TestRunnerChoice(
                TestRunnerKind.Plugin,
                string.IsNullOrWhiteSpace(runner.Value.Name) ? runner.Value.Id : runner.Value.Name,
                runner.Value.Id,
                runner.Value.RequiresDevice,
                runner.Value.SupportsRerun));
        }

        return choices;
    }
}
