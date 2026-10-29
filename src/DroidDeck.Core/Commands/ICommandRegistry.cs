namespace DroidDeck.Core.Commands;

/// <summary>
/// The registry of palette-visible commands. Thread-safe; plugins may register
/// from any thread and the app listens to change notifications.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>Gets a snapshot of the registered commands in registration order.</summary>
    IReadOnlyList<CommandDefinition> Commands { get; }

    /// <summary>Finds a command by its stable identifier.</summary>
    CommandDefinition? Find(string commandId);

    /// <summary>Registers a command. Duplicate identifiers are rejected.</summary>
    void Register(CommandDefinition definition);

    /// <summary>
    /// Removes a command, for example when its plugin is disabled.
    /// Returns <see langword="true"/> when a command was removed.
    /// </summary>
    bool Unregister(string commandId);

    /// <summary>Raised after commands are registered or removed.</summary>
    event EventHandler? Changed;
}
