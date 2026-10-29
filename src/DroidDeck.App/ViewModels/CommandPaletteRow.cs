using DroidDeck.Core.Commands;
using Microsoft.UI.Xaml;

namespace DroidDeck.App.ViewModels;

/// <summary>One row in the command palette result list.</summary>
public sealed class CommandPaletteRow
{
    /// <summary>Creates a row for a command and its effective shortcut.</summary>
    public CommandPaletteRow(CommandDefinition command, string shortcut)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Shortcut = shortcut;
    }

    /// <summary>Gets the command the row invokes.</summary>
    public CommandDefinition Command { get; }

    /// <summary>Gets the effective shortcut text, or empty when unbound.</summary>
    public string Shortcut { get; }

    /// <summary>Gets the secondary line: category plus description.</summary>
    public string Detail => Command.Description.Length == 0
        ? Command.Category
        : $"{Command.Category} · {Command.Description}";

    /// <summary>Gets whether a shortcut hint badge is shown.</summary>
    public Visibility ShortcutVisibility => Shortcut.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
