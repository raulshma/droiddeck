using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.App.ViewModels;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Screen;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace DroidDeck.App.Views;

/// <summary>Shows connected devices, their overview, per-device notes, and plugin actions.</summary>
public sealed partial class DevicesPage : Page, IRefreshablePage, IDisposable
{
    private readonly IThumbnailService _thumbnails;
    private readonly IConfigurationService _configuration;
    private readonly PluginHost _plugins;
    private readonly IShellDialogs _dialogs;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _thumbnailCts;
    private bool _disposed;

    /// <summary>Creates the devices page.</summary>
    public DevicesPage(
        DevicesViewModel viewModel,
        IThumbnailService thumbnails,
        IConfigurationService configuration,
        PluginHost plugins,
        IShellDialogs dialogs)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        DataContext = viewModel;
        ViewModel.Devices.CollectionChanged += OnDevicesCollectionChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _plugins.Registry.Changed += OnPluginRegistryChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RebuildPluginActions();
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
        StopThumbnailLoop();
        ViewModel.Devices.CollectionChanged -= OnDevicesCollectionChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _plugins.Registry.Changed -= OnPluginRegistryChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.Dispose();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnRestartAdbClick(object sender, RoutedEventArgs e) => await ViewModel.RestartAdbAsync();

    private async void OnRefreshMetadataClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RefreshMetadataAsync();

    private async void OnSaveNotesClick(object sender, RoutedEventArgs e) => await ViewModel.SaveNotesAsync();

    private async void OnSaveTagsClick(object sender, RoutedEventArgs e) => await ViewModel.SaveTagsAsync();

    private void OnOpenToolboxClick(object sender, RoutedEventArgs e) => OpenDeviceTool("devices/tools");

    private void OnOpenMirrorClick(object sender, RoutedEventArgs e) => OpenDeviceTool("devices/mirror");

    private void OnOpenEmulatorsClick(object sender, RoutedEventArgs e) => OpenDeviceTool("devices/emulators");

    private void OnOpenWirelessClick(object sender, RoutedEventArgs e) => OpenDeviceTool("devices/wireless");

    private void OpenDeviceTool(string key)
    {
        ViewModel.OpenSelectedDevice();
        App.Instance.Navigate(key);
    }

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

    private void OnLoaded(object sender, RoutedEventArgs e) => StartThumbnailLoop();

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopThumbnailLoop();

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            StartThumbnailLoop();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(DevicesViewModel.SelectedDevice), StringComparison.Ordinal))
        {
            RebuildPluginActions();
        }
    }

    private void OnPluginRegistryChanged(object? sender, EventArgs e) => Dispatch(RebuildPluginActions);

    private void StartThumbnailLoop()
    {
        StopThumbnailLoop();
        var screen = _configuration.Current.Settings.Screen;
        if (!screen.ThumbnailsEnabled)
        {
            return;
        }

        var serials = ViewModel.Devices
            .Select(device => device.Serial)
            .ToArray();
        if (serials.Length == 0)
        {
            return;
        }

        var fps = Math.Clamp(screen.ThumbnailFps, 0.2, 5);
        var interval = TimeSpan.FromSeconds(1 / fps);
        var cts = new CancellationTokenSource();
        _thumbnailCts = cts;
        _ = RunThumbnailLoopAsync(serials, interval, cts);
    }

    private async Task RunThumbnailLoopAsync(
        IReadOnlyCollection<string> serials,
        TimeSpan interval,
        CancellationTokenSource cts)
    {
        try
        {
            await _thumbnails.RunAsync(serials, interval, UpdateThumbnailAsync, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ViewModel.SetErrorMessage($"Device thumbnails stopped: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCts, cts))
            {
                _thumbnailCts = null;
            }

            cts.Dispose();
        }
    }

    private void StopThumbnailLoop()
    {
        var cts = _thumbnailCts;
        if (cts is null)
        {
            return;
        }

        _thumbnailCts = null;
        cts.Cancel();
    }

    private Task UpdateThumbnailAsync(ScreenThumbnail thumbnail)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            return UpdateThumbnailCoreAsync(thumbnail);
        }

        var completion = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    await UpdateThumbnailCoreAsync(thumbnail);
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetResult();
        }

        return completion.Task;
    }

    private async Task UpdateThumbnailCoreAsync(ScreenThumbnail thumbnail)
    {
        if (_disposed)
        {
            return;
        }

        var row = ViewModel.Devices.FirstOrDefault(device =>
            string.Equals(device.Serial, thumbnail.Serial, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(thumbnail.Png);
            await writer.StoreAsync();
            _ = writer.DetachStream();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        row.Thumbnail = image;
    }

    private void RebuildPluginActions()
    {
        PluginActionsPanel.Children.Clear();
        var entries = _plugins.Extensions.DeviceActions;
        PluginActionsEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in entries)
        {
            var button = new Button
            {
                Content = $"{entry.Action.Name} · {entry.SourceLabel}",
                Tag = entry,
                IsEnabled = ViewModel.SelectedDevice is { IsReady: true },
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(button, $"PluginDeviceAction_{entry.Action.Id}");
            ToolTipService.SetToolTip(button, $"Plugin: {entry.SourceLabel}");
            button.Click += OnPluginActionClick;
            PluginActionsPanel.Children.Add(button);
        }
    }

    private async void OnPluginActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceActionEntry entry } ||
            ViewModel.SelectedDevice is not { IsReady: true } device)
        {
            return;
        }

        if (entry.Action.RequiresConfirmation &&
            !await _dialogs.ConfirmAsync(
                "Run plugin action?",
                $"{entry.Action.Name} from {entry.SourceLabel} changes device state. Run it on {device.DisplayName}?"))
        {
            return;
        }

        ViewModel.ClearMessages();
        var result = await _plugins.Invoker.InvokeDeviceActionAsync(
            entry.PluginId,
            entry.Action,
            new DeviceActionContext(device.Serial, device.Model));
        if (result.Succeeded)
        {
            ViewModel.SetActionMessage(
                $"Ran {entry.Action.Name} from {entry.SourceLabel} ({result.Duration.TotalMilliseconds:F0} ms).");
        }
        else
        {
            ViewModel.SetErrorMessage($"{entry.Action.Name} from {entry.SourceLabel} failed: {result.Error}");
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }
}
