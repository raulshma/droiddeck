using System.Text;
using DroidDeck.App.Commands;
using DroidDeck.Core.Commands;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DroidDeck.App.Views;

/// <summary>
/// Captures a shortcut chord of one to three gestures for a command. Esc closes
/// the dialog; the captured chord is returned via <see cref="CapturedChord"/>.
/// </summary>
public sealed partial class ShortcutCaptureDialog : ContentDialog
{
    private readonly List<KeyGesture> _steps = [];

    /// <summary>Creates the capture dialog for one command.</summary>
    public ShortcutCaptureDialog(CommandDefinition command, KeyChord? current)
    {
        ArgumentNullException.ThrowIfNull(command);
        InitializeComponent();
        InstructionText.Text = $"Press the key combination to use for '{command.Title}'.";
        CaptureText.Text = current?.ToString() ?? "Press keys…";
        IsPrimaryButtonEnabled = false;
    }

    /// <summary>Gets the captured chord, or <see langword="null"/> before a valid capture.</summary>
    public KeyChord? CapturedChord => _steps.Count > 0 ? KeyChord.Create([.. _steps]) : null;

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
            CaptureText.Text = BuildDisplay(FormatPreview(ShortcutKeys.CurrentModifiers()));
            e.Handled = true;
            return;
        }

        if (_steps.Count >= KeyChord.MaximumSteps)
        {
            CaptureText.Text = BuildDisplay($"A chord is at most {KeyChord.MaximumSteps} combinations. Clear it first.");
            e.Handled = true;
            return;
        }

        if (!ShortcutKeys.TryGetToken(e.Key, out var token))
        {
            CaptureText.Text = BuildDisplay("That key is not supported. Try a letter, number, function key, or named key.");
            IsPrimaryButtonEnabled = false;
            e.Handled = true;
            return;
        }

        _steps.Add(new KeyGesture(ShortcutKeys.CurrentModifiers(), token));
        CaptureText.Text = BuildDisplay(null);
        IsPrimaryButtonEnabled = true;
        e.Handled = true;
    }

    private void OnClearStepsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _steps.Clear();
        IsPrimaryButtonEnabled = false;
        CaptureText.Text = "Press keys…";
    }

    private string BuildDisplay(string? notice)
    {
        var builder = new StringBuilder(string.Join("  ", _steps.Select(step => step.ToString())));
        if (_steps.Count > 0 && _steps.Count < KeyChord.MaximumSteps && notice is null)
        {
            builder.Append("   (press the next combination, or save)");
        }

        if (notice is not null)
        {
            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(notice);
        }

        return builder.ToString();
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
}
