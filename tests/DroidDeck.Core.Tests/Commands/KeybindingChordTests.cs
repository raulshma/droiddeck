using DroidDeck.Core.Commands;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Commands;

public sealed class KeybindingChordTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly CommandRegistry _registry = new();
    private readonly string _filePath;

    public KeybindingChordTests()
    {
        _registry.Register(Create("shell.run", "Run", KeyGesture.Parse("F5")));
        _registry.Register(Create("shell.stop", "Stop", KeyGesture.Parse("Shift+F5")));
        _registry.Register(Create("shell.clear", "Clear", KeyGesture.Parse("Ctrl+L")));
        _filePath = _temp.Combine("keybindings.json");
    }

    [Fact]
    public async Task ChordOverridePersistsAndResolvesWithoutRestart()
    {
        var map = new KeybindingMap(_registry, _filePath);
        var chord = KeyChord.Parse("Ctrl+K Ctrl+C");

        Assert.True(map.SetOverride("shell.run", chord).Applied);
        await map.SaveAsync();

        Assert.Equal(chord, map.GetEffectiveChord("shell.run"));
        Assert.Null(map.GetEffectiveShortcut("shell.run"));

        var restarted = new KeybindingMap(_registry, _filePath);
        restarted.EnsureLoaded();
        Assert.Equal(chord, restarted.GetEffectiveChord("shell.run"));
        Assert.Equal(chord, restarted.GetOverrideChord("shell.run"));
        Assert.Null(restarted.GetOverride("shell.run"));
    }

    [Fact]
    public void ChordConflictsAreRejectedAndReported()
    {
        var map = new KeybindingMap(_registry, _filePath);
        var chord = KeyChord.Parse("Ctrl+K Ctrl+C");
        map.SetOverride("shell.run", chord);

        var outcome = map.SetOverride("shell.stop", chord);

        Assert.False(outcome.Applied);
        Assert.Equal("shell.run", outcome.ConflictingWith?.Id);
        Assert.Equal(KeyGesture.Parse("Shift+F5"), map.GetEffectiveShortcut("shell.stop"));
    }

    [Fact]
    public void FindChordConflictsIncludesMultiStepCollisions()
    {
        _registry.Register(Create("shell.other", "Other", KeyChord.Parse("Ctrl+K Ctrl+C")));
        _registry.Register(Create("shell.third", "Third", KeyChord.Parse("Ctrl+K Ctrl+C")));        var map = new KeybindingMap(_registry, _filePath);

        var conflicts = map.FindChordConflicts();

        var conflict = Assert.Single(conflicts);
        Assert.Equal(KeyChord.Parse("Ctrl+K Ctrl+C"), conflict.Chord);
        Assert.Equal(2, conflict.Commands.Count);
    }

    [Fact]
    public async Task ExportImportAndResetAllRoundTrip()
    {
        var map = new KeybindingMap(_registry, _filePath);
        map.SetOverride("shell.run", KeyChord.Parse("Ctrl+Alt+R"));
        var exported = map.Export();

        var restarted = new KeybindingMap(_registry, _filePath);
        var imported = restarted.Import(exported);
        await restarted.SaveAsync();

        Assert.Contains("shell.run", imported.Applied);
        Assert.Empty(imported.Skipped);
        Assert.Equal(KeyChord.Parse("Ctrl+Alt+R"), restarted.GetEffectiveChord("shell.run"));
        Assert.Equal(1, restarted.ResetAll());
        Assert.Equal(KeyGesture.Parse("F5"), restarted.GetEffectiveShortcut("shell.run"));
    }

    [Fact]
    public void ImportSkipsUnknownCommandsAndInvalidChords()
    {
        var map = new KeybindingMap(_registry, _filePath);

        var result = map.Import(
            """{ "schemaVersion": 1, "overrides": { "plugin.gone": "Ctrl+Alt+G", "shell.run": "Ctrl+Shift" } }""");

        Assert.Empty(result.Applied);
        Assert.Contains("plugin.gone", result.Skipped);
        Assert.Contains("shell.run", result.Skipped);
    }

    [Fact]
    public async Task ImportReplaceClearsOverridesAndRejectsConflictingEntries()
    {
        var map = new KeybindingMap(_registry, _filePath);
        map.SetOverride("shell.clear", KeyGesture.Parse("Ctrl+Alt+C"));
        await map.SaveAsync();

        // Shift+F5 collides with shell.stop's default, so the entry is rejected
        // even though Replace mode just cleared the previous overrides.
        var result = map.Import(
            """
            { "schemaVersion": 1, "overrides": { "shell.clear": "Shift+F5", "shell.run": "Ctrl+Alt+R" } }
            """,
            KeybindingImportMode.Replace);

        Assert.Contains("shell.run", result.Applied);
        Assert.Contains("shell.clear", result.Skipped);
        Assert.Single(result.Conflicts);
        // Replace mode removed the previous override, and the rejected entry
        // leaves the factory default in place.
        Assert.Equal(KeyGesture.Parse("Ctrl+L"), map.GetEffectiveShortcut("shell.clear"));
        Assert.Equal(KeyChord.Parse("Ctrl+Alt+R"), map.GetEffectiveChord("shell.run"));
    }

    private static CommandDefinition Create(string id, string title, KeyGesture shortcut) =>
        new(id, title, "Test", _ => Task.CompletedTask) { DefaultShortcut = shortcut };

    private static CommandDefinition Create(string id, string title, KeyChord chord) =>
        new(id, title, "Test", _ => Task.CompletedTask) { DefaultChord = chord };

    public void Dispose() => _temp.Dispose();
}
