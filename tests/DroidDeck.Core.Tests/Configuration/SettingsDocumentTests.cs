using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class SettingsDocumentTests
{
    private static readonly string[] ExpectedReplaced =
    [
        "appearance.theme", "appearance.density", "appearance.empty", "scalar", "section", "new",
    ];

    private static readonly string[] ExpectedLeaves =
    [
        "appearance.theme", "appearance.density", "appearance.empty", "scalar", "section", "new.leaf",
    ];

    [Fact]
    public void GetSetAndRemoveMatchDottedPathsIgnoringCase()
    {
        var document = Parse(
            """
            { "appearance": { "theme": "dark", "Density": "compact" } }
            """);

        Assert.True(SettingsDocument.TryGet(document, "APPEARANCE.THEME", out var theme));
        Assert.Equal("dark", theme!.GetValue<string>());

        SettingsDocument.Set(document, "Appearance.Theme", JsonValue.Create("light"));

        var appearance = document["appearance"]!.AsObject();
        Assert.Equal("theme", SettingsDocument.FindKey(appearance, "THEME"));
        Assert.True(SettingsDocument.TryGetProperty(appearance, "density", out var density));
        Assert.Equal("compact", density!.GetValue<string>());
        Assert.Equal("light", appearance["theme"]!.GetValue<string>());
        Assert.False(appearance.ContainsKey("Theme"));
        Assert.False(document.ContainsKey("Appearance"));

        Assert.True(SettingsDocument.Remove(document, "appearance.DENSITY"));
        Assert.False(appearance.ContainsKey("Density"));
        Assert.False(SettingsDocument.Remove(document, "appearance.density"));
    }

    [Fact]
    public void SetCreatesParentsAndReplacesNonObjectSegments()
    {
        var document = new JsonObject { ["section"] = new JsonObject { ["value"] = 5 } };

        SettingsDocument.Set(document, "section.value.leaf", "nested");
        SettingsDocument.Set(document, "missing.parent.leaf", true);

        Assert.Equal("nested", document["section"]!["value"]!["leaf"]!.GetValue<string>());
        Assert.True(document["missing"]!["parent"]!["leaf"]!.GetValue<bool>());
    }

    [Fact]
    public void RemoveReturnsFalseForMissingPathsAndLeavesValuesInPlace()
    {
        var document = Parse("""{ "section": { "value": 1 } }""");

        Assert.False(SettingsDocument.Remove(document, "section.missing"));
        Assert.False(SettingsDocument.Remove(document, "missing.value"));
        Assert.Equal(1, document["section"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public void TryGetStringRejectsNonStringValues()
    {
        var document = Parse(
            """
            { "section": { "text": "hello", "number": 5, "flag": true, "nothing": null } }
            """);

        Assert.True(SettingsDocument.TryGetString(document, "SECTION.TEXT", out var text));
        Assert.Equal("hello", text);
        Assert.False(SettingsDocument.TryGetString(document, "section.number", out var number));
        Assert.Null(number);
        Assert.False(SettingsDocument.TryGetString(document, "section.flag", out _));
        Assert.False(SettingsDocument.TryGetString(document, "section.nothing", out _));
        Assert.False(SettingsDocument.TryGetString(document, "section.missing", out _));
    }

    [Fact]
    public void FlattenTreatsEverythingButNonEmptyObjectsAsLeaves()
    {
        var document = Parse(
            """
            {
              "nested": { "leaf": 1, "deeper": { "leaf": "two" } },
              "list": [1, { "x": 2 }],
              "empty": {},
              "nothing": null
            }
            """);

        var leaves = SettingsDocument.Flatten(document);

        Assert.Equal(1, leaves["nested.leaf"]!.GetValue<int>());
        Assert.Equal("two", leaves["nested.deeper.leaf"]!.GetValue<string>());
        Assert.Equal(1, leaves["list"]![0]!.GetValue<int>());
        Assert.Equal(2, leaves["list"]![1]!["x"]!.GetValue<int>());
        Assert.IsType<JsonObject>(leaves["empty"]);
        Assert.Empty(leaves["empty"]!.AsObject());
        Assert.True(leaves.TryGetValue("nothing", out var nothing));
        Assert.Null(nothing);
        Assert.False(leaves.ContainsKey(string.Empty));
        Assert.Equal(5, leaves.Count);

        Assert.Empty(SettingsDocument.Flatten(new JsonObject()));
    }

    [Fact]
    public void FlattenLeavesAreDeepClones()
    {
        var document = Parse("""{ "section": { "value": "original" }, "list": [1] }""");

        var leaves = SettingsDocument.Flatten(document);
        document["section"]!["value"] = "changed";
        document["list"]![0] = 2;

        Assert.Equal("original", leaves["section.value"]!.GetValue<string>());
        Assert.Equal(1, leaves["list"]![0]!.GetValue<int>());
    }

    [Fact]
    public void MergeRecursesThroughObjectsAndReportsEveryReplacement()
    {
        var target = Parse(
            """
            {
              "appearance": { "theme": "dark", "Density": "compact" },
              "kept": { "nested": 1 },
              "scalar": 5,
              "section": { "a": 1 }
            }
            """);
        var overrides = Parse(
            """
            {
              "appearance": { "theme": "light", "density": "spacious", "empty": {} },
              "kept": {},
              "scalar": [1, 2],
              "section": "flat",
              "new": { "leaf": true }
            }
            """);
        var replaced = new List<string>();
        var leaves = new List<string>();

        SettingsDocument.Merge(
            target,
            overrides,
            onReplaced: replaced.Add,
            onLeaf: (path, _) => leaves.Add(path));

        Assert.Equal(ExpectedReplaced, replaced);
        Assert.Equal(ExpectedLeaves, leaves);

        Assert.Equal("light", target["appearance"]!["theme"]!.GetValue<string>());
        var appearance = target["appearance"]!.AsObject();
        Assert.True(appearance.ContainsKey("Density"));
        Assert.False(appearance.ContainsKey("density"));
        Assert.Equal("spacious", appearance["Density"]!.GetValue<string>());
        Assert.Empty(appearance["empty"]!.AsObject());
        Assert.Equal(1, target["kept"]!["nested"]!.GetValue<int>());
        Assert.Equal(2, target["scalar"]!.AsArray().Count);
        Assert.Equal("flat", target["section"]!.GetValue<string>());
        Assert.True(target["new"]!["leaf"]!.GetValue<bool>());
    }

    [Fact]
    public void MergeAssignsDeepClonesOfReplacedValues()
    {
        var target = Parse("""{ "value": "old" }""");
        var overrides = Parse("""{ "value": "new", "list": [1] }""");

        SettingsDocument.Merge(target, overrides);
        overrides["value"] = "mutated";
        overrides["list"]![0] = 2;

        Assert.Equal("new", target["value"]!.GetValue<string>());
        Assert.Equal(1, target["list"]![0]!.GetValue<int>());
    }

    [Fact]
    public void FlattenedLeavesCanBeWrittenBackIntoAnEquivalentDocument()
    {
        var document = Parse(
            """
            {
              "appearance": { "theme": "dark", "list": [1, 2], "empty": {} },
              "nothing": null
            }
            """);

        var rebuilt = new JsonObject();
        foreach (var pair in SettingsDocument.Flatten(document))
        {
            SettingsDocument.Set(rebuilt, pair.Key, pair.Value);
        }

        Assert.True(JsonNode.DeepEquals(document, rebuilt));
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
