using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.Plugin.UI.Kit;

/// <summary>
/// The WinUI InfoBar with the shell's defaults, so plugins show notices with
/// the same appearance and severity colors the host uses.
/// </summary>
public sealed class PluginInfoBar : InfoBar
{
    /// <summary>Creates a closed info bar.</summary>
    public PluginInfoBar()
    {
        IsClosable = true;
        IsOpen = false;
    }

    /// <summary>Shows a notice.</summary>
    /// <param name="message">The notice text.</param>
    /// <param name="severity">The notice severity.</param>
    /// <param name="title">An optional title.</param>
    public void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Severity = severity;
        Title = title ?? string.Empty;
        Message = message;
        IsOpen = true;
    }

    /// <summary>Closes the notice.</summary>
    public void Close() => IsOpen = false;
}
