using System.Diagnostics;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.Tests.Rendering;

/// <summary>
/// The 100 ms budget for plugin UI creation. The kit controls themselves are
/// WinUI objects the xunit host cannot construct (no XAML runtime), so this
/// budgets the non-visual creation path the host runs once per view:
/// isolation, timeout bookkeeping, health recording, and the fallback decision
/// the fault boundary acts on.
/// </summary>
public sealed class PluginUiCreationBudgetTests
{
    private const int Iterations = 50;
    private const double BudgetMilliseconds = 100;

    [Fact]
    public async Task PluginViewCreationStaysWithinTheHundredMillisecondBudget()
    {
        var invoker = new PluginActionInvoker(new PluginHealthTracker());
        var page = new FastPage();
        var timings = new List<TimeSpan>(Iterations);

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await invoker.CreateViewAsync(
                "com.example.budget",
                "navigation page",
                token => page.CreateViewAsync(token));

            Assert.True(result.Succeeded, result.Error);
            timings.Add(Stopwatch.GetElapsedTime(started));
        }

        AssertBudget(timings, "view creation");
    }

    [Fact]
    public async Task TheFallbackDecisionPathStaysWithinTheHundredMillisecondBudget()
    {
        var invoker = new PluginActionInvoker(new PluginHealthTracker());
        var timings = new List<TimeSpan>(Iterations);

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await invoker.CreateViewAsync(
                "com.example.budget",
                "navigation page",
                _ => throw new InvalidOperationException("The view factory failed."));
            var elapsed = Stopwatch.GetElapsedTime(started);

            Assert.False(result.Succeeded);
            Assert.NotNull(result.Error);
            // A failed result is exactly what makes the fault boundary show its
            // fallback card, so the failure handling must fit the same budget.
            timings.Add(elapsed);
        }

        AssertBudget(timings, "the fallback decision path");
    }

    private static void AssertBudget(List<TimeSpan> timings, string subject)
    {
        var average = timings.Average(static timing => timing.TotalMilliseconds);
        var p95 = timings.Order().ElementAt((int)(Iterations * 0.95) - 1).TotalMilliseconds;
        Assert.True(
            average < BudgetMilliseconds,
            $"{subject} averaged {average:0.00} ms, above the {BudgetMilliseconds:0} ms budget.");
        Assert.True(
            p95 < BudgetMilliseconds,
            $"{subject} p95 was {p95:0.00} ms, above the {BudgetMilliseconds:0} ms budget.");
    }

    private sealed class FastPage : INavigationPage
    {
        public string Id => "com.example.budget.page";

        public string Title => "Budget page";

        public string Icon => string.Empty;

        public Task<object> CreateViewAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>(new object());
    }
}
