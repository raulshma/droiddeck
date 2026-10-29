namespace DroidDeck.Core.Commands;

/// <summary>Thread-safe <see cref="ICommandRegistry"/> implementation.</summary>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly object _sync = new();
    private readonly List<CommandDefinition> _commands = [];

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<CommandDefinition> Commands
    {
        get
        {
            lock (_sync)
            {
                return _commands.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public CommandDefinition? Find(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (_sync)
        {
            return _commands.FirstOrDefault(
                command => string.Equals(command.Id, commandId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public void Register(CommandDefinition definition)
    {
        Validate(definition);
        lock (_sync)
        {
            if (_commands.Any(command =>
                    string.Equals(command.Id, definition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"A command with id '{definition.Id}' is already registered.", nameof(definition));
            }

            _commands.Add(definition);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool Unregister(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        bool removed;
        lock (_sync)
        {
            var index = _commands.FindIndex(command =>
                string.Equals(command.Id, commandId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            _commands.RemoveAt(index);
            removed = true;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    private static void Validate(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Title);
        ArgumentNullException.ThrowIfNull(definition.Execute);
    }
}
