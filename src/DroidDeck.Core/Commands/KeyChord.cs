using System.Text;

namespace DroidDeck.Core.Commands;

/// <summary>
/// An ordered sequence of one to three gestures, for example <c>Ctrl+K Ctrl+C</c>.
/// A chord matches only when every step is pressed in order.
/// </summary>
public sealed class KeyChord : IEquatable<KeyChord>
{
    /// <summary>The maximum number of steps a chord may contain.</summary>
    public const int MaximumSteps = 3;

    private KeyChord(IReadOnlyList<KeyGesture> steps)
    {
        Steps = steps;
    }

    /// <summary>Gets the gestures in press order.</summary>
    public IReadOnlyList<KeyGesture> Steps { get; }

    /// <summary>Gets whether the chord is a single gesture.</summary>
    public bool IsSingleStep => Steps.Count == 1;

    /// <summary>Creates a chord from one or more gestures.</summary>
    public static KeyChord Create(params KeyGesture[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Length is < 1 or > MaximumSteps)
        {
            throw new ArgumentException($"A chord needs between 1 and {MaximumSteps} gestures.", nameof(steps));
        }

        if (steps.Any(static step => step is null))
        {
            throw new ArgumentException("A chord step cannot be null.", nameof(steps));
        }

        return new KeyChord(steps.ToArray());
    }

    /// <summary>Creates a single-step chord from a gesture.</summary>
    public static KeyChord FromGesture(KeyGesture gesture) =>
        new([gesture ?? throw new ArgumentNullException(nameof(gesture))]);

    /// <summary>Parses a chord, throwing when malformed.</summary>
    public static KeyChord Parse(string text)
    {
        if (!TryParse(text, out var chord))
        {
            throw new ArgumentException(
                $"'{text}' is not a shortcut chord. Use one to three gestures separated by spaces, such as Ctrl+K Ctrl+C.",
                nameof(text));
        }

        return chord;
    }

    /// <summary>Attempts to parse a chord separated by spaces or commas.</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = new KeyChord([]);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var tokens = text.Split(
            [' ', ',', '\t'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > MaximumSteps)
        {
            return false;
        }

        var steps = new List<KeyGesture>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!KeyGesture.TryParse(token, out var gesture))
            {
                return false;
            }

            steps.Add(gesture);
        }

        chord = new KeyChord(steps);
        return true;
    }

    /// <summary>Returns whether both chords contain the same gestures in the same order.</summary>
    public static bool operator ==(KeyChord? left, KeyChord? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Returns whether the chords differ.</summary>
    public static bool operator !=(KeyChord? left, KeyChord? right) => !(left == right);

    /// <inheritdoc />
    public bool Equals(KeyChord? other)
    {
        if (other is null || other.Steps.Count != Steps.Count)
        {
            return false;
        }

        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index] != other.Steps[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is KeyChord other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var step in Steps)
        {
            hash.Add(step);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var step in Steps)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(step);
        }

        return builder.ToString();
    }
}
