using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DroidDeck.App.Services;

/// <summary>
/// The WinUI implementation of <see cref="IShellDialogs"/>. The shell window is
/// resolved lazily so the service can be constructed before the window exists,
/// one dialog is presented at a time, and every picker is initialized with the
/// window handle WinUI requires for unpackaged apps.
/// </summary>
public sealed class ShellDialogs : IShellDialogs
{
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly Func<Window?> _window;
    private readonly ILogger<ShellDialogs> _logger;
    private int _dialogOpen;

    /// <summary>
    /// Creates the dialog service. The window providers are evaluated when a
    /// dialog or picker is used, so the service can be built while the window
    /// itself is still being constructed.
    /// </summary>
    /// <param name="xamlRoot">Resolves the shell's current XAML root.</param>
    /// <param name="window">Resolves the shell window for picker initialization.</param>
    /// <param name="logger">The logger for blocked-dialog warnings.</param>
    public ShellDialogs(
        Func<XamlRoot?> xamlRoot,
        Func<Window?> window,
        ILogger<ShellDialogs> logger)
    {
        _xamlRoot = xamlRoot ?? throw new ArgumentNullException(nameof(xamlRoot));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(
        string title,
        string message,
        string primaryButtonText = "Continue",
        string closeButtonText = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await ShowAsync(dialog).ConfigureAwait(true);
        return result.Result == ContentDialogResult.Primary;
    }

    /// <inheritdoc />
    public async Task<ShellDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
        {
            _logger.LogWarning(
                "A dialog could not be opened because another dialog is already active.");
            return new ShellDialogResult(ContentDialogResult.None, WasBlocked: true);
        }

        try
        {
            var xamlRoot = _xamlRoot();
            if (xamlRoot is null)
            {
                _logger.LogWarning("The shell window is not available for dialogs.");
                return new ShellDialogResult(ContentDialogResult.None, WasBlocked: false);
            }

            dialog.XamlRoot = xamlRoot;
            return new ShellDialogResult(await dialog.ShowAsync(), WasBlocked: false);
        }
        catch (InvalidOperationException exception)
        {
            // A dialog that did not go through this service is already open.
            _logger.LogWarning(
                exception,
                "A dialog could not be opened because another dialog is already active.");
            return new ShellDialogResult(ContentDialogResult.None, WasBlocked: true);
        }
        finally
        {
            Interlocked.Exchange(ref _dialogOpen, 0);
        }
    }

    /// <inheritdoc />
    public void InitializePicker(FileOpenPicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        Initialize(picker);
    }

    /// <inheritdoc />
    public void InitializePicker(FileSavePicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        Initialize(picker);
    }

    /// <inheritdoc />
    public void InitializePicker(FolderPicker picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        Initialize(picker);
    }

    private void Initialize(object picker)
    {
        var window = _window()
            ?? throw new InvalidOperationException("The shell window is not available for pickers.");
        var handle = WindowNative.GetWindowHandle(window);
        InitializeWithWindow.Initialize(picker, handle);
    }
}
