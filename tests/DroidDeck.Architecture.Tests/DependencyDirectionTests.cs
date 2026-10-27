namespace DroidDeck.Architecture.Tests;

public sealed class DependencyDirectionTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        ["DroidDeck.Plugin.Abstractions"] = [],
        ["DroidDeck.Core"] = ["DroidDeck.Plugin.Abstractions"],
        ["DroidDeck.Adb"] = ["DroidDeck.Core", "DroidDeck.Plugin.Abstractions"],
        ["DroidDeck.Projects"] = ["DroidDeck.Core", "DroidDeck.Plugin.Abstractions"],
        ["DroidDeck.Screen"] = ["DroidDeck.Core", "DroidDeck.Plugin.Abstractions"],
        ["DroidDeck.Plugin.UI"] = ["DroidDeck.Plugin.Abstractions"],
        ["DroidDeck.Plugins"] = ["DroidDeck.Core", "DroidDeck.Plugin.Abstractions", "DroidDeck.Plugin.UI"],
        ["DroidDeck.App"] =
        [
            "DroidDeck.Core",
            "DroidDeck.Adb",
            "DroidDeck.Projects",
            "DroidDeck.Screen",
            "DroidDeck.Plugins",
            "DroidDeck.Plugin.Abstractions",
            "DroidDeck.Plugin.UI",
        ],
    };

    [Fact]
    public void EverySourceProjectIsCoveredByTheLayerRules()
    {
        var graph = RepositoryLayout.LoadSourceProjectGraph();

        Assert.Equal(
            AllowedReferences.Keys.Order(StringComparer.Ordinal),
            graph.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SourceProjectsOnlyReferenceLowerLayers()
    {
        var graph = RepositoryLayout.LoadSourceProjectGraph();
        var violations = new List<string>();

        foreach (var (project, references) in graph)
        {
            if (!AllowedReferences.TryGetValue(project, out var allowed))
            {
                violations.Add($"{project} has no dependency rule.");
                continue;
            }

            var permitted = allowed.ToHashSet(StringComparer.Ordinal);
            foreach (var reference in references.Where(reference => !permitted.Contains(reference)))
            {
                violations.Add($"{project} must not reference {reference}.");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Dependency direction violations:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void NoSourceProjectReferencesTheShell()
    {
        var graph = RepositoryLayout.LoadSourceProjectGraph();
        var offenders = graph
            .Where(pair => pair.Key != "DroidDeck.App" && pair.Value.Contains("DroidDeck.App", StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();

        Assert.Empty(offenders);
    }
}
