using DroidDeck.Core.Commands;

namespace DroidDeck.App.Commands;

/// <summary>
/// Routes multi-step shortcut chords such as Ctrl+K Ctrl+C to their commands.
/// WinUI accelerators only match single gestures, so the window forwards key
/// presses here; the router keeps the list of chords that still match the keys
/// pressed so far and resets on a non-match or after a short timeout,
/// mirroring editor-style chord UX.
/// </summary>
public sealed class ChordRouter
{
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<KeyGesture, List<ChordBinding>> _bindings = [];
    private List<ChordBinding>? _pending;
    private int _progress;
    private DateTimeOffset _pendingAt;

    /// <summary>
    /// Rebuilds the routing table from the commands whose effective chord has
    /// more than one step. Single-step chords stay with keyboard accelerators.
    /// </summary>
    public void Rebuild(IEnumerable<(CommandDefinition Command, KeyChord Chord)> chords)
    {
        _bindings.Clear();
        foreach (var (command, chord) in chords)
        {
            if (chord is not { IsSingleStep: false })
            {
                continue;
            }

            var first = chord.Steps[0];
            if (!_bindings.TryGetValue(first, out var list))
            {
                list = [];
                _bindings[first] = list;
            }

            list.Add(new ChordBinding(command, chord));
        }

        Reset();
    }

    /// <summary>Gets whether one or more chords are mid-sequence.</summary>
    public bool HasPendingStep => _pending is not null;

    /// <summary>
    /// Offers a key press to the router. Returns the command whose chord
    /// completed, or <see langword="null"/> when the key did not complete a chord.
    /// A key that matches no in-progress chord restarts matching as a first step.
    /// </summary>
    public CommandDefinition? OfferKey(KeyGesture gesture)
    {
        var now = DateTimeOffset.UtcNow;
        if (_pending is not null && now - _pendingAt > PendingTimeout)
        {
            Reset();
        }

        var candidates = _pending is null
            ? null
            : _pending
                .Where(binding => binding.Chord.Steps.Count > _progress &&
                    binding.Chord.Steps[_progress] == gesture)
                .ToList();
        _pendingAt = now;

        if (candidates is null || candidates.Count == 0)
        {
            // The key either starts a new chord or is unrelated; either way only
            // a fresh first-step match is kept so typing stays unaffected.
            _pending = _bindings.TryGetValue(gesture, out var fresh) ? fresh : null;
            _progress = 1;
            return null;
        }

        _progress++;
        var completed = candidates
            .Where(binding => binding.Chord.Steps.Count == _progress)
            .Select(binding => (CommandDefinition?)binding.Command)
            .FirstOrDefault();
        if (completed is not null)
        {
            Reset();
            return completed;
        }

        _pending = candidates;
        return null;
    }

    /// <summary>Clears any pending chord, for example when a menu opens.</summary>
    public void Reset()
    {
        _pending = null;
        _progress = 0;
    }

    private readonly record struct ChordBinding(CommandDefinition Command, KeyChord Chord);
}
