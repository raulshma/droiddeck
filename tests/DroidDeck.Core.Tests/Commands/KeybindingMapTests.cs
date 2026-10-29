using DroidDeck.Core.Commands;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Commands;

public sealed class KeybindingMapTests : IDisposable
{
    private static readonly KeyGesture RunDefault = KeyGesture.Parse("F5");
    private static readonly KeyGesture StopDefault = KeyGesture.Parse("Shift+F5");

    private readonly TempDirectory _temp = new();
    private readonly CommandRegistry _registry = new();
    private readonly CommandDefinition _run;
    private readonly CommandDefinition _stop;
    private readonly string _filePath;

    public KeybindingMapTests()
    {
        _run = Create("shell.run", "Run", RunDefault);
        _stop = Create("shell.stop", "Stop", StopDefault);
        _registry.Register(_run);
        _registry.Register(_stop);
        _filePath = _temp.Combine("keybindings.json");
    }

    [Fact]
    public void EffectiveShortcutFallsBackToTheRegisteredDefault()
    {
        var map = CreateMap();

        Assert.Null(map.GetOverride("shell.run"));
        Assert.Equal(RunDefault, map.GetEffectiveShortcut("shell.run"));
        Assert.Equal(StopDefault, map.GetEffectiveShortcut(_stop));
    }

    [Fact]
    public void OverrideChangesTheEffectiveBinding()
    {
        var map = CreateMap();
        var replacement = KeyGesture.Parse("Ctrl+Alt+R");

        var outcome = map.SetOverride("shell.run", replacement);

        Assert.True(outcome.Applied);
        Assert.Null(outcome.ConflictingWith);
        Assert.Equal(replacement, map.GetOverride("shell.run"));
        Assert.Equal(replacement, map.GetEffectiveShortcut("shell.run"));
    }

    [Fact]
    public async Task OverridesPersistAcrossRestart()
    {
        var replacement = KeyGesture.Parse("Ctrl+Alt+R");
        var map = CreateMap();
        map.SetOverride("shell.run", replacement);
        await map.SaveAsync();

        var restarted = CreateMap();
        restarted.EnsureLoaded();

        Assert.Equal(replacement, restarted.GetEffectiveShortcut("shell.run"));
        Assert.Null(restarted.GetOverride("shell.stop"));
    }

    [Fact]
    public async Task ConflictingOverrideIsRejectedAndNotPersisted()
    {
        var map = CreateMap();

        var outcome = map.SetOverride("shell.stop", RunDefault);

        Assert.False(outcome.Applied);
        Assert.Same(_run, outcome.ConflictingWith);
        Assert.Equal(StopDefault, map.GetEffectiveShortcut("shell.stop"));
        await map.SaveAsync();

        var restarted = CreateMap();
        restarted.EnsureLoaded();
        Assert.Null(restarted.GetOverride("shell.stop"));
    }

    [Fact]
    public void RebindingToTheCurrentEffectiveGestureIsAcceptedWithoutChange()
    {
        var map = CreateMap();

        var outcome = map.SetOverride("shell.run", RunDefault);

        Assert.True(outcome.Applied);
        Assert.Null(outcome.ConflictingWith);
        Assert.Null(map.GetOverride("shell.run"));
    }

    [Fact]
    public void SetOverrideForAnUnknownCommandIsRejected()
    {
        var map = CreateMap();

        var outcome = map.SetOverride("shell.missing", KeyGesture.Parse("Ctrl+M"));

        Assert.False(outcome.Applied);
        Assert.Null(outcome.ConflictingWith);
    }

    [Fact]
    public void ResetRestoresTheDefault()
    {
        var map = CreateMap();
        map.SetOverride("shell.run", KeyGesture.Parse("Ctrl+Alt+R"));

        Assert.True(map.Reset("shell.run"));
        Assert.False(map.Reset("shell.run"));

        Assert.Null(map.GetOverride("shell.run"));
        Assert.Equal(RunDefault, map.GetEffectiveShortcut("shell.run"));
    }

    [Fact]
    public void ConflictsAreReportedForCommandsSharingAGesture()
    {
        var secondRunner = Create("shell.run2", "Run again", RunDefault);
        _registry.Register(secondRunner);
        var map = CreateMap();

        var conflicts = map.FindConflicts();

        var conflict = Assert.Single(conflicts);
        Assert.Equal(KeyGesture.Parse("F5"), conflict.Gesture);
        Assert.Contains(conflict.Commands, command => command.Id == "shell.run");
        Assert.Contains(conflict.Commands, command => command.Id == "shell.run2");
    }

    [Fact]
    public void LoadToleratesAMissingFile()
    {
        var map = CreateMap();

        map.EnsureLoaded();

        Assert.Null(map.LastLoadNotice);
        Assert.Equal(RunDefault, map.GetEffectiveShortcut("shell.run"));
    }

    [Fact]
    public void LoadToleratesACorruptFileAndRecoversWithDefaults()
    {
        _temp.WriteFile("keybindings.json", "{ this is not json");
        var map = CreateMap();

        map.EnsureLoaded();

        Assert.NotNull(map.LastLoadNotice);
        Assert.Null(map.GetOverride("shell.run"));
        Assert.Equal(RunDefault, map.GetEffectiveShortcut("shell.run"));
    }

    [Fact]
    public async Task LoadDropsOverridesForCommandsThatNoNotExist()
    {
        var replacement = KeyGesture.Parse("Ctrl+Alt+R");
        var map = CreateMap();
        map.SetOverride("shell.run", replacement);
        map.SetOverride("plugin.gone", KeyGesture.Parse("Ctrl+Alt+G"));
        await map.SaveAsync();

        var restarted = CreateMap();
        restarted.EnsureLoaded();

        Assert.Equal(replacement, restarted.GetOverride("shell.run"));
        Assert.Null(restarted.GetOverride("plugin.gone"));
    }

    [Fact]
    public async Task LoadDropsSavedOverridesThatCollideWithAnotherCommand()
    {
        // Simulates a default changed between versions: the saved override was
        // valid when written but now collides with another command's binding.
        _temp.WriteFile(
            "keybindings.json",
            """
            {
              "schemaVersion": 1,
              "overrides": {
                "shell.stop": "F5",
                "shell.run": "Ctrl+Alt+R"
              }
            }
            """);

        var restarted = CreateMap();
        restarted.EnsureLoaded();

        Assert.Null(restarted.GetOverride("shell.stop"));
        Assert.Equal(StopDefault, restarted.GetEffectiveShortcut("shell.stop"));
        Assert.Equal(KeyGesture.Parse("Ctrl+Alt+R"), restarted.GetOverride("shell.run"));
        Assert.Contains("Stop", restarted.LastLoadNotice);
        Assert.Contains("Run", restarted.LastLoadNotice);
    }

    [Fact]
    public void ChangesRaiseTheChangedEvent()
    {
        var map = CreateMap();
        var changed = 0;
        map.Changed += (_, _) => changed++;

        map.SetOverride("shell.run", KeyGesture.Parse("Ctrl+Alt+R"));
        map.Reset("shell.run");

        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task SaveAsyncWritesValidJsonNextToTheOtherSettingsFiles()
    {
        var map = CreateMap();
        map.SetOverride("shell.run", KeyGesture.Parse("Ctrl+Alt+R"));

        await map.SaveAsync();

        var json = await File.ReadAllTextAsync(_filePath);
        Assert.Contains("\"shell.run\"", json);
        Assert.Contains("Ctrl+Alt+R", json);
    }

    private KeybindingMap CreateMap() => new(_registry, _filePath);

    private static CommandDefinition Create(string id, string title, KeyGesture shortcut) =>
        new(id, title, "Test", _ => Task.CompletedTask) { DefaultShortcut = shortcut };

    public void Dispose() => _temp.Dispose();
}
