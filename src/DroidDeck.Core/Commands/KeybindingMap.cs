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

/// <summary>
/// Resolves effective shortcuts from registered command defaults and user
/// overrides. Overrides are rejected when they collide with another command's
/// effective shortcut (plan 12.9: later binding rejected), and persist as JSON
/// next to the other settings files via <see cref="AtomicFile"/>.
/// </summary>
public sealed class KeybindingMap
{
    private readonly ICommandRegistry _registry;
    private readonly string _filePath;
    private readonly object _sync = new();
    private readonly Dictionary<string, KeyGesture> _overrides = new(StringComparer.OrdinalIgnoreCase);
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
                foreach (var (commandId, gestureText) in document.Overrides)
                {
                    if (!KeyGesture.TryParse(gestureText, out var gesture))
                    {
                        continue;
                    }

                    var command = _registry.Find(commandId);
                    if (command is null)
                    {
                        continue;
                    }

                    // Saved overrides follow the same conflict rule as live
                    // rebinding: an override whose gesture collides with
                    // another command's effective shortcut is discarded, so a
                    // default changed between versions cannot produce two
                    // commands sharing one shortcut.
                    var conflicting = _registry.Commands.FirstOrDefault(other =>
                        !string.Equals(other.Id, command.Id, StringComparison.OrdinalIgnoreCase) &&
                        (_overrides.TryGetValue(other.Id, out var existing) ? existing : other.DefaultShortcut) == gesture);
                    if (conflicting is not null)
                    {
                        notices.Add(
                            $"The {command.Title} shortcut {gestureText} was ignored because {conflicting.Title} already uses it.");
                        continue;
                    }

                    _overrides[command.Id] = gesture;
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
            foreach (var (commandId, gesture) in _overrides)
            {
                document.Overrides[commandId] = gesture.ToString();
            }
        }

        await AtomicFile.WriteAllTextAsync(
            _filePath,
            JsonSerializer.Serialize(document, KeybindingsJson.Options),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the user override for a command, when one exists.</summary>
    public KeyGesture? GetOverride(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (_sync)
        {
            return _overrides.TryGetValue(commandId, out var gesture) ? gesture : null;
        }
    }

    /// <summary>Gets the override or, without one, the command's default shortcut.</summary>
    public KeyGesture? GetEffectiveShortcut(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return GetOverride(command.Id) ?? command.DefaultShortcut;
    }

    /// <summary>Gets the effective shortcut for a registered command identifier.</summary>
    public KeyGesture? GetEffectiveShortcut(string commandId)
    {
        var command = _registry.Find(commandId);
        return command is null ? null : GetEffectiveShortcut(command);
    }

    /// <summary>
    /// Applies an override unless another command already resolves to the same
    /// gesture; the conflicting command is reported and nothing changes.
    /// </summary>
    public KeybindingAssignment SetOverride(string commandId, KeyGesture gesture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentNullException.ThrowIfNull(gesture);

        var command = _registry.Find(commandId);
        if (command is null)
        {
            return new KeybindingAssignment(false, null);
        }

        lock (_sync)
        {
            if (gesture == (GetOverride(command.Id) ?? command.DefaultShortcut))
            {
                return new KeybindingAssignment(true, null);
            }

            var conflicting = _registry.Commands.FirstOrDefault(other =>
                !string.Equals(other.Id, command.Id, StringComparison.OrdinalIgnoreCase) &&
                (GetOverride(other.Id) ?? other.DefaultShortcut) == gesture);
            if (conflicting is not null)
            {
                return new KeybindingAssignment(false, conflicting);
            }

            _overrides[command.Id] = gesture;
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

    /// <summary>Lists every effective-shortcut collision across registered commands.</summary>
    public IReadOnlyList<KeybindingConflict> FindConflicts()
    {
        Dictionary<KeyGesture, List<CommandDefinition>> groups = new();
        foreach (var command in _registry.Commands)
        {
            var gesture = GetEffectiveShortcut(command);
            if (gesture is null)
            {
                continue;
            }

            if (!groups.TryGetValue(gesture, out var group))
            {
                group = [];
                groups[gesture] = group;
            }

            group.Add(command);
        }

        return groups
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => new KeybindingConflict(pair.Value.ToArray(), pair.Key))
            .ToArray();
    }
}
