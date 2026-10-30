using System.Text.Json;
using DroidDeck.Core.Infrastructure;

namespace DroidDeck.Core.Commands;

/// <summary>The outcome of a rebinding attempt.</summary>
/// <param name="Applied">Whether the override was accepted.</param>
/// <param name="ConflictingWith">The command already holding the requested shortcut, when rejected.</param>
public sealed record KeybindingAssignment(bool Applied, CommandDefinition? ConflictingWith);

/// <summary>A collision between two or more commands sharing one effective shortcut.</summary>
/// <param name="Commands">The commands sharing the gesture.</param>
/// <param name="Gesture">The contested gesture.</param>
public sealed record KeybindingConflict(IReadOnlyList<CommandDefinition> Commands, KeyGesture Gesture);

/// <summary>A collision between commands sharing one effective chord.</summary>
/// <param name="Commands">The commands sharing the chord.</param>
/// <param name="Chord">The contested chord.</param>
public sealed record KeybindingChordConflict(IReadOnlyList<CommandDefinition> Commands, KeyChord Chord);

/// <summary>How imported keybindings combine with existing overrides.</summary>
public enum KeybindingImportMode
{
    /// <summary>Keep existing overrides and add or update the imported ones.</summary>
    Merge,

    /// <summary>Remove all overrides before applying the imported ones.</summary>
    Replace,
}

/// <summary>The result of importing a keybindings document.</summary>
/// <param name="Applied">Commands whose imported shortcut was accepted.</param>
/// <param name="Skipped">Commands skipped because the document was invalid, the command is unknown, or the shortcut conflicts.</param>
/// <param name="Conflicts">Conflicts discovered while importing.</param>
public sealed record KeybindingImportResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<KeybindingChordConflict> Conflicts);

/// <summary>
/// Resolves effective shortcuts from registered command defaults and user
/// overrides. Overrides are rejected when they collide with another command's
/// effective shortcut (plan 12.9: later binding rejected), and persist as JSON
/// next to the other settings files via <see cref="AtomicFile"/>.
/// Single gestures and multi-step chords share one conflict namespace.
/// </summary>
public sealed class KeybindingMap
{
    private readonly ICommandRegistry _registry;
    private readonly string _filePath;
    private readonly object _sync = new();
    private readonly Dictionary<string, KeyChord> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    /// <summary>Creates the map over a command registry and a JSON file path.</summary>
    public KeybindingMap(ICommandRegistry registry, string filePath)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <summary>Gets the JSON file the overrides persist to.</summary>
    public string FilePath => _filePath;

    /// <summary>Gets the notice describing why saved overrides were discarded, if any.</summary>
    public string? LastLoadNotice { get; private set; }

    /// <summary>Raised after an override is added or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Loads saved overrides once. A missing file keeps the defaults; an
    /// unreadable file is discarded and reported through <see cref="LastLoadNotice"/>.
    /// </summary>
    public void EnsureLoaded()
    {
        lock (_sync)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
        }

        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<KeybindingDocument>(File.ReadAllText(_filePath), KeybindingsJson.Options);
            if (document?.Overrides is not { Count: > 0 })
            {
                return;
            }

            lock (_sync)
            {
                var notices = new List<string>();
                foreach (var (commandId, chordText) in document.Overrides)
                {
                    if (!KeyChord.TryParse(chordText, out var chord))
                    {
                        continue;
                    }

                    var command = _registry.Find(commandId);
                    if (command is null)
                    {
                        continue;
                    }

                    // Saved overrides follow the same conflict rule as live
                    // rebinding: an override whose chord collides with another
                    // command's effective shortcut is discarded, so a default
                    // changed between versions cannot produce two commands
                    // sharing one shortcut.
                    var conflicting = _registry.Commands.FirstOrDefault(other =>
                        !string.Equals(other.Id, command.Id, StringComparison.OrdinalIgnoreCase) &&
                        EffectiveChordUnsafe(other) == chord);
                    if (conflicting is not null)
                    {
                        notices.Add(
                            $"The {command.Title} shortcut {chordText} was ignored because {conflicting.Title} already uses it.");
                        continue;
                    }

                    _overrides[command.Id] = chord;
                }

                if (notices.Count > 0)
                {
                    LastLoadNotice = string.Join(" ", notices);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            lock (_sync)
            {
                _overrides.Clear();
                LastLoadNotice =
                    $"Keybindings in {Path.GetFileName(_filePath)} could not be read; defaults were restored.";
            }
        }
    }

    /// <summary>Writes the current overrides atomically, keeping a backup.</summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        KeybindingDocument document;
        lock (_sync)
        {
            document = new KeybindingDocument();
            foreach (var (commandId, chord) in _overrides)
            {
                document.Overrides[commandId] = chord.ToString();
            }
        }

        await AtomicFile.WriteAllTextAsync(
            _filePath,
            JsonSerializer.Serialize(document, KeybindingsJson.Options),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exports every user override as a portable JSON document. The document can
    /// be imported on another machine; unknown commands are skipped on import.
    /// </summary>
    public string Export()
    {
        KeybindingDocument document;
        lock (_sync)
        {
            document = new KeybindingDocument();
            foreach (var (commandId, chord) in _overrides)
            {
                document.Overrides[commandId] = chord.ToString();
            }
        }

        return JsonSerializer.Serialize(document, KeybindingsJson.Options);
    }

    /// <summary>Imports a previously exported keybindings document.</summary>
    public KeybindingImportResult Import(string json, KeybindingImportMode mode = KeybindingImportMode.Merge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        KeybindingDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<KeybindingDocument>(json, KeybindingsJson.Options);
        }
        catch (JsonException)
        {
            throw new KeybindingImportException("The keybindings document is not valid JSON.");
        }

        if (document is null)
        {
            throw new KeybindingImportException("The keybindings document is empty.");
        }

        var applied = new List<string>();
        var skipped = new List<string>();
        var conflicts = new List<KeybindingChordConflict>();

        lock (_sync)
        {
            if (mode == KeybindingImportMode.Replace)
            {
                _overrides.Clear();
            }

            foreach (var (commandId, chordText) in document.Overrides)
            {
                if (!KeyChord.TryParse(chordText, out var chord) ||
                    _registry.Find(commandId) is not { } command)
                {
                    skipped.Add(commandId);
                    continue;
                }

                if (chord == EffectiveChordUnsafe(command))
                {
                    applied.Add(commandId);
                    continue;
                }

                var conflicting = _registry.Commands.FirstOrDefault(other =>
                    !string.Equals(other.Id, command.Id, StringComparison.OrdinalIgnoreCase) &&
                    EffectiveChordUnsafe(other) == chord);
                if (conflicting is not null)
                {
                    skipped.Add(commandId);
                    conflicts.Add(new KeybindingChordConflict([conflicting], chord));
                    continue;
                }

                _overrides[command.Id] = chord;
                applied.Add(commandId);
            }

            if (applied.Count > 0)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        return new KeybindingImportResult(applied, skipped, conflicts);
    }

    /// <summary>Gets the user override for a command when it is a single gesture.</summary>
    public KeyGesture? GetOverride(string commandId)
    {
        var chord = GetOverrideChord(commandId);
        return chord is { IsSingleStep: true } ? chord.Steps[0] : null;
    }

    /// <summary>Gets the full user override chord for a command, when one exists.</summary>
    public KeyChord? GetOverrideChord(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (_sync)
        {
            return _overrides.TryGetValue(commandId, out var chord) ? chord : null;
        }
    }

    /// <summary>Gets the override or, without one, the command's default shortcut.</summary>
    public KeyGesture? GetEffectiveShortcut(CommandDefinition command)
    {
        var chord = GetEffectiveChord(command);
        return chord is { IsSingleStep: true } ? chord.Steps[0] : null;
    }

    /// <summary>Gets the effective shortcut for a registered command identifier.</summary>
    public KeyGesture? GetEffectiveShortcut(string commandId)
    {
        var command = _registry.Find(commandId);
        return command is null ? null : GetEffectiveShortcut(command);
    }

    /// <summary>Gets the full effective chord, including a chord override.</summary>
    public KeyChord? GetEffectiveChord(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            return EffectiveChordUnsafe(command);
        }
    }

    /// <summary>Gets the full effective chord for a registered command identifier.</summary>
    public KeyChord? GetEffectiveChord(string commandId)
    {
        var command = _registry.Find(commandId);
        return command is null ? null : GetEffectiveChord(command);
    }

    /// <summary>
    /// Applies a single-gesture override unless another command already resolves
    /// to the same chord; the conflicting command is reported and nothing changes.
    /// </summary>
    public KeybindingAssignment SetOverride(string commandId, KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        return SetOverride(commandId, KeyChord.FromGesture(gesture));
    }

    /// <summary>
    /// Applies a chord override unless another command already resolves to the
    /// same chord; the conflicting command is reported and nothing changes.
    /// </summary>
    public KeybindingAssignment SetOverride(string commandId, KeyChord chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(chord);

        var command = _registry.Find(commandId);
        if (command is null)
        {
            return new KeybindingAssignment(false, null);
        }

        lock (_sync)
        {
            if (chord == EffectiveChordUnsafe(command))
            {
                return new KeybindingAssignment(true, null);
            }

            var conflicting = _registry.Commands.FirstOrDefault(other =>
                !string.Equals(other.Id, command.Id, StringComparison.OrdinalIgnoreCase) &&
                EffectiveChordUnsafe(other) == chord);
            if (conflicting is not null)
            {
                return new KeybindingAssignment(false, conflicting);
            }

            _overrides[command.Id] = chord;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new KeybindingAssignment(true, null);
    }

    /// <summary>
    /// Removes a command's override so its default applies again.
    /// Returns <see langword="true"/> when an override was removed.
    /// </summary>
    public bool Reset(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (_sync)
        {
            if (!_overrides.Remove(commandId))
            {
                return false;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Removes every override and restores all command defaults.</summary>
    public int ResetAll()
    {
        int removed;
        lock (_sync)
        {
            removed = _overrides.Count;
            _overrides.Clear();
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>Lists every effective-shortcut collision, including chords.</summary>
    public IReadOnlyList<KeybindingChordConflict> FindChordConflicts()
    {
        Dictionary<KeyChord, List<CommandDefinition>> groups = [];
        foreach (var command in _registry.Commands)
        {
            var chord = GetEffectiveChord(command);
            if (chord is null)
            {
                continue;
            }

            if (!groups.TryGetValue(chord, out var group))
            {
                group = [];
                groups[chord] = group;
            }

            group.Add(command);
        }

        return groups
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => new KeybindingChordConflict(pair.Value.ToArray(), pair.Key))
            .ToArray();
    }

    /// <summary>Lists collisions between commands using the same single gesture.</summary>
    public IReadOnlyList<KeybindingConflict> FindConflicts() =>
        FindChordConflicts()
            .Where(static conflict => conflict.Chord.IsSingleStep)
            .Select(static conflict => new KeybindingConflict(conflict.Commands, conflict.Chord.Steps[0]))
            .ToArray();

    private KeyChord? EffectiveChordUnsafe(CommandDefinition command) =>
        _overrides.TryGetValue(command.Id, out var chord)
            ? chord
            : command.DefaultChord
                ?? (command.DefaultShortcut is { } gesture ? KeyChord.FromGesture(gesture) : null);
}

/// <summary>Raised when an imported keybindings document cannot be read.</summary>
public sealed class KeybindingImportException(string message) : Exception(message);
