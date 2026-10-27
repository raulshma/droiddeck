using System.Runtime.InteropServices;
using DroidDeck.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>Shows connected devices, their overview, and per-device notes.</summary>
public sealed partial class DevicesPage : Page, IRefreshablePage, IDisposable
{
    private bool _disposed;

    /// <summary>Creates the devices page.</summary>
    public DevicesPage(DevicesViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>Gets the page view model.</summary>
    public DevicesViewModel ViewModel { get; }

    /// <inheritdoc />
    public void Refresh() => ViewModel.Refresh();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ViewModel.Dispose();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnRestartAdbClick(object sender, RoutedEventArgs e) => await ViewModel.RestartAdbAsync();

    private async void OnRefreshMetadataClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshMetadataAsync();

    private async void OnSaveNotesClick(object sender, RoutedEventArgs e) => await ViewModel.SaveNotesAsync();

    private async void OnSaveTagsClick(object sender, RoutedEventArgs e) => await ViewModel.SaveTagsAsync();

    private void OnCopySerialClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDevice is { } device)
        {
            CopyText(device.Serial, $"Serial {device.Serial} copied.");
        }
    }

    private void OnCopySummaryClick(object sender, RoutedEventArgs e) =>
        CopyText(ViewModel.BuildSelectedDiagnostics(), "Device summary copied with the diagnostics ID.");

    private void CopyText(string text, string successMessage)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ViewModel.SetActionMessage(successMessage);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            ViewModel.SetErrorMessage(
                $"Copying to the clipboard failed. Diagnostics ID {App.Instance.Session.DiagnosticsId}.");
        }
    }
}
