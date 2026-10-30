using System.Diagnostics;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class RendererCreationBudgetTests
{
    private const int Iterations = 50;

    [Fact]
    public void RendererCreationStaysWithinTheFiftyMillisecondBudget()
    {
        var catalog = new RendererCatalog();
        catalog.Register(new RendererDescriptor(
            "builtin.chart",
            PluginConstantsBuiltIn,
            RendererKind.Chart,
            "Built-in chart"));
        catalog.Register(new RendererDescriptor(
            "plugin.chart",
            "com.example",
            RendererKind.Chart,
            "Plugin chart",
            Priority: 50));

        var timings = new List<TimeSpan>(Iterations);
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = RendererRunner.Render(
                catalog,
                RendererKind.Chart,
                preferredId: null,
                canRender: null,
                create: _ =>
                {
                    var renderer = new BudgetRenderer();
                    try
                    {
                        return renderer.CreateView(null!) ?? new object();
                    }
                    finally
                    {
                        renderer.Dispose();
                    }
                });

            Assert.NotNull(result.View);
            timings.Add(Stopwatch.GetElapsedTime(started));
        }

        var average = timings.Average(static timing => timing.TotalMilliseconds);
        var p95 = timings.Order().ElementAt((int)(Iterations * 0.95) - 1).TotalMilliseconds;
        Assert.True(
            average < 50,
            $"Renderer creation averaged {average:0.00} ms, above the 50 ms budget.");
        Assert.True(
            p95 < 50,
            $"Renderer creation p95 was {p95:0.00} ms, above the 50 ms budget.");
    }

    private const string PluginConstantsBuiltIn = PluginConstants.BuiltInPluginId;

    private sealed class BudgetRenderer : IRenderer
    {
        public string Id => "plugin.chart";

        public string DisplayName => "Budget renderer";

        public RendererKind Kind => RendererKind.Chart;

        public int Priority => 50;

        public bool CanRender(IRenderContext context) => true;

        public FrameworkElement CreateView(IRenderContext context) => null!;

        public void Dispose()
        {
        }
    }
}
