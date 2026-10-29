using DroidDeck.App.ViewModels;
using DroidDeck.Core.Commands;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DroidDeck.App.Controls;

/// <summary>
/// The Ctrl+K command palette overlay. Created once by the shell and shown by
/// toggling visibility; commands are snapshotted and only re-ranked per keystroke
/// so opening stays inside the 50 ms budget.
/// </summary>
public sealed partial class CommandPaletteOverlay : UserControl
{
    private readonly ICommandRegistry _commands;
    private readonly KeybindingMap _keybindings;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private IReadOnlyList<CommandDefinition> _snapshot = [];
    private bool _stale = true;

    /// <summary>Creates the palette over a command registry and keybinding map.</summary>
    public CommandPaletteOverlay(ICommandRegistry commands, KeybindingMap keybindings)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _keybindings = keybindings ?? throw new ArgumentNullException(nameof(keybindings));
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        _commands.Changed += OnRegistryChanged;
        _keybindings.Changed += OnKeybindingsChanged;
    }

    /// <summary>Raised when the user picks a command to run.</summary>
    public event EventHandler<CommandDefinition>? ExecuteRequested;

    /// <summary>Raised after the palette becomes visible.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised after the palette is hidden.</summary>
    public event EventHandler? Closed;

    /// <summary>Gets whether the palette is currently shown.</summary>
    public bool IsOpen => OverlayRoot.Visibility == Visibility.Visible;

    /// <summary>Shows the palette and focuses the query box.</summary>
    public void Open()
    {
        if (IsOpen)
        {
            return;
        }

        OverlayRoot.Visibility = Visibility.Visible;
        QueryBox.Text = string.Empty;
        ApplyFilter();
        QueryBox.Focus(FocusState.Keyboard);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides the palette.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        OverlayRoot.Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRegistryChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(MarkStale);

    private void OnKeybindingsChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(MarkStale);

    private void MarkStale()
    {
        _stale = true;
        if (IsOpen)
        {
            ApplyFilter();
        }
    }

    private void OnQueryTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_stale)
        {
            _stale = false;
            _snapshot = _commands.Commands;
        }

        var rows = CommandSearcher.Search(_snapshot, QueryBox.Text)
            .Select(result => new CommandPaletteRow(
                result.Command,
                _keybindings.GetEffectiveShortcut(result.Command)?.ToString() ?? string.Empty))
            .ToArray();
        ResultsList.ItemsSource = rows;
        if (rows.Length > 0)
        {
            ResultsList.SelectedIndex = 0;
        }

        EmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                InvokeSelection();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void OnOverlayKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => Close();

    private void OnResultItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandPaletteRow row)
        {
            ExecuteRequested?.Invoke(this, row.Command);
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, ResultsList.Items.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void InvokeSelection()
    {
        if (ResultsList.SelectedItem is CommandPaletteRow row)
        {
            ExecuteRequested?.Invoke(this, row.Command);
        }
    }
}
