namespace DroidDeck.Core.Commands;

/// <summary>
/// A palette-visible command: stable identity, search metadata, a default
/// shortcut, an optional enablement hook, and the action to run.
/// </summary>
/// <param name="Id">The stable identifier, for example <c>shell.run</c>.</param>
/// <param name="Title">The display title shown in the palette and shortcut lists.</param>
/// <param name="Category">The grouping label, for example <c>Run</c> or <c>Navigate</c>.</param>
/// <param name="Execute">The action invoked when the command runs.</param>
public sealed record CommandDefinition(
    string Id,
    string Title,
    string Category,
    Func<CancellationToken, Task> Execute)
{
    /// <summary>Gets or sets the longer description used by search.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets synonyms and keywords the command can be found by.</summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Gets or sets the factory default shortcut, when the command has one.</summary>
    public KeyGesture? DefaultShortcut { get; init; }

    /// <summary>Gets or sets a factory default chord, when the command needs more than one gesture.</summary>
    public KeyChord? DefaultChord { get; init; }

    /// <summary>Gets or sets the hook that reports whether the command currently applies.</summary>
    public Func<bool>? IsEnabledPredicate { get; init; }

    /// <summary>Gets whether the command currently applies; commands without a hook are always enabled.</summary>
    public bool IsEnabled => IsEnabledPredicate?.Invoke() ?? true;
}
