using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DroidDeck.App.Services;

/// <summary>
/// The shell's single entry point for confirmations, content dialogs, and
/// window-handle initialization of file pickers.
/// </summary>
public interface IShellDialogs
{
    /// <summary>
    /// Shows a confirmation dialog with a primary and a close button.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The dialog body.</param>
    /// <param name="primaryButtonText">The primary button text.</param>
    /// <param name="closeButtonText">The close button text.</param>
    /// <returns><see langword="true"/> when the primary button was chosen.</returns>
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "Continue",
        string closeButtonText = "Cancel");

    /// <summary>Shows a prepared dialog on the shell window.</summary>
    /// <param name="dialog">The dialog to show.</param>
    /// <returns>
    /// The button chosen; <see cref="ShellDialogResult.WasBlocked"/> is set when
    /// another dialog is already open.
    /// </returns>
    Task<ShellDialogResult> ShowAsync(ContentDialog dialog);

    /// <summary>Initializes an open-file picker with the shell window handle.</summary>
    void InitializePicker(FileOpenPicker picker);

    /// <summary>Initializes a save-file picker with the shell window handle.</summary>
    void InitializePicker(FileSavePicker picker);

    /// <summary>Initializes a folder picker with the shell window handle.</summary>
    void InitializePicker(FolderPicker picker);
}

/// <summary>The outcome of <see cref="IShellDialogs.ShowAsync"/>.</summary>
/// <param name="Result">The button chosen, or None when no dialog appeared.</param>
/// <param name="WasBlocked">Whether another dialog is already open.</param>
public readonly record struct ShellDialogResult(ContentDialogResult Result, bool WasBlocked);
