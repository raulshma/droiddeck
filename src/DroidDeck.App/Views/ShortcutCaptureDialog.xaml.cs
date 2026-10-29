using DroidDeck.App.Commands;
using DroidDeck.Core.Commands;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI.Core;
using Windows.System;

namespace DroidDeck.App.Views;

/// <summary>
/// Captures a key combination for a command shortcut. Esc closes the dialog;
/// the captured gesture is returned via <see cref="CapturedShortcut"/>.
/// </summary>
public sealed partial class ShortcutCaptureDialog : ContentDialog
{
    private KeyGesture? _captured;

    /// <summary>Creates the capture dialog for one command.</summary>
    public ShortcutCaptureDialog(CommandDefinition command, KeyGesture? current)
    {
        ArgumentNullException.ThrowIfNull(command);
        InitializeComponent();
        InstructionText.Text = $"Press the key combination to use for '{command.Title}'.";
        CaptureText.Text = current?.ToString() ?? "Press keys…";
        IsPrimaryButtonEnabled = false;
    }

    /// <summary>Gets the captured gesture, or <see langword="null"/> before a valid capture.</summary>
    public KeyGesture? CapturedShortcut => _captured;

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (ShortcutKeys.IsModifier(e.Key))
        {
            CaptureText.Text = FormatPreview(CurrentModifiers());
            e.Handled = true;
            return;
        }

        if (!ShortcutKeys.TryGetToken(e.Key, out var token))
        {
            CaptureText.Text = "That key is not supported. Try a letter, number, function key, or named key.";
            IsPrimaryButtonEnabled = false;
            e.Handled = true;
            return;
        }

        _captured = new KeyGesture(CurrentModifiers(), token);
        CaptureText.Text = _captured.ToString();
        IsPrimaryButtonEnabled = true;
        e.Handled = true;
    }

    private static string FormatPreview(KeyModifiers modifiers) => modifiers switch
    {
        KeyModifiers.None => "Press keys…",
        KeyModifiers.Control => "Ctrl+…",
        KeyModifiers.Shift => "Shift+…",
        KeyModifiers.Control | KeyModifiers.Shift => "Ctrl+Shift+…",
        KeyModifiers.Alt => "Alt+…",
        _ => modifiers.ToString(),
    };

    private static KeyModifiers CurrentModifiers()
    {
        KeyModifiers modifiers = KeyModifiers.None;
        if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.LeftControl) || IsDown(VirtualKey.RightControl))
        {
            modifiers |= KeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift) || IsDown(VirtualKey.LeftShift) || IsDown(VirtualKey.RightShift))
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftMenu) || IsDown(VirtualKey.RightMenu))
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= KeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
