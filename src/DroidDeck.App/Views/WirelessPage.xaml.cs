using System.Globalization;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Wireless;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DroidDeck.App.Views;

/// <summary>
/// Drives QR and manual wireless adb pairing, mDNS discovery, remembered
/// devices, and the automatic reconnect loop.
/// </summary>
public sealed partial class WirelessPage : Page, IRefreshablePage, IDisposable
{
    private readonly IWirelessAdbService _wireless;
    private readonly IConfigurationService _configuration;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _qrCts;
    private WirelessPairingSession? _pairingSession;
    private bool _initializing;
    private bool _disposed;

    /// <summary>Creates the wireless page.</summary>
    public WirelessPage(
        IWirelessAdbService wireless,
        IConfigurationService configuration)
    {
        _wireless = wireless ?? throw new ArgumentNullException(nameof(wireless));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        _wireless.StateChanged += OnWirelessStateChanged;
        RefreshState();
    }

    /// <inheritdoc />
    public void Refresh() => RefreshState();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wireless.StateChanged -= OnWirelessStateChanged;
        _qrCts?.Cancel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshState();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _qrCts?.Cancel();
    }

    private void OnWirelessStateChanged(object? sender, EventArgs e) => Dispatch(RefreshState);

    private void RefreshState()
    {
        var services = _wireless.DiscoveredServices
            .Select(service => new ServiceRow(service))
            .ToArray();
        ServiceList.ItemsSource = services;
        ServiceEmptyText.Visibility = services.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var devices = _wireless.RememberedDevices
            .Select(device => new DeviceRow(device))
            .ToArray();
        RememberedList.ItemsSource = devices;
        RememberedEmptyText.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        _initializing = true;
        try
        {
            AutoReconnectToggle.IsOn = _wireless.IsReconnecting;
        }
        finally
        {
            _initializing = false;
        }

        ErrorBar.Message = _wireless.LastError ?? string.Empty;
        ErrorBar.IsOpen = !string.IsNullOrWhiteSpace(_wireless.LastError);
    }

    private async void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Discovering mDNS services...");
        try
        {
            var services = await _wireless.DiscoverAsync();
            RefreshState();
            ShowResult(
                true,
                services.Count == 0
                    ? "No mDNS services were found. Keep the Wireless debugging screen open on the device."
                    : $"Discovered {services.Count} wireless service(s).",
                null,
                services.Count == 0
                    ? "Both the computer and the device must be on the same network, and the device must show Wireless debugging."
                    : null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Discovery failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnGenerateQrClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Creating a pairing session...");
        try
        {
            _pairingSession = _wireless.CreatePairingSession();
            await ShowQrAsync(_pairingSession.QrPng);
            PairingCodeText.Text = $"Pairing code {_pairingSession.PairingCode}";
            PairingCodeText.Visibility = Visibility.Visible;
            PairingSessionText.Text =
                $"Service {_pairingSession.ServiceName} expires at {_pairingSession.ExpiresAt.ToLocalTime():HH:mm:ss}. " +
                "Keep the QR scanner open until the device is paired.";
            WaitForScanButton.IsEnabled = true;
            PairingStatusText.Text = "Ready. Scan the code, then press Wait for scan and pair.";
            ShowGuidance(
                "The device must be on the same Wi-Fi network. The pairing service disappears when the QR scanner closes.");
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The pairing session could not be created: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ShowQrAsync(byte[]? png)
    {
        if (png is not { Length: > 0 })
        {
            QrImage.Source = null;
            QrImage.Visibility = Visibility.Collapsed;
            return;
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        QrImage.Source = bitmap;
        QrImage.Visibility = Visibility.Visible;
    }

    private async void OnWaitForScanClick(object sender, RoutedEventArgs e)
    {
        if (_pairingSession is not { } session)
        {
            ShowResult(false, "Generate a pairing QR code first.", null, null);
            return;
        }

        _qrCts?.Cancel();
        var cts = new CancellationTokenSource();
        _qrCts = cts;
        var token = cts.Token;
        WaitForScanButton.IsEnabled = false;
        CancelQrButton.IsEnabled = true;
        SetBusy(true, "Waiting for the device to scan the QR code...");
        try
        {
            var progress = new Progress<string>(message => PairingStatusText.Text = message);
            var result = await _wireless.CompleteQrPairingAsync(session, progress, token);
            _pairingSession = null;
            ShowResult(result);
            PairingStatusText.Text = result.Message;
            WaitForScanButton.IsEnabled = false;
            RefreshState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            PairingStatusText.Text = "Pairing was cancelled. The same QR code can still be scanned before it expires.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"QR pairing failed: {exception.Message}", null, null);
        }
        finally
        {
            CancelQrButton.IsEnabled = false;
            if (_pairingSession is not null)
            {
                WaitForScanButton.IsEnabled = true;
            }

            if (ReferenceEquals(_qrCts, cts))
            {
                _qrCts = null;
            }

            cts.Dispose();
            SetBusy(false);
        }
    }

    private void OnCancelQrClick(object sender, RoutedEventArgs e)
    {
        _qrCts?.Cancel();
        PairingStatusText.Text = "Cancelling the QR wait...";
    }

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        var address = PairAddressBox.Text.Trim();
        var code = PairCodeBox.Text.Trim();
        if (address.Length == 0 || code.Length == 0)
        {
            ShowResult(
                false,
                "Enter the pairing address and code from the device.",
                null,
                "Open Wireless debugging → Pair device with pairing code to see both values.");
            return;
        }

        SetBusy(true, $"Pairing with {address}...");
        try
        {
            var result = await _wireless.PairAsync(address, code, remember: true);
            ShowResult(result);
            if (result.Succeeded)
            {
                PairCodeBox.Text = string.Empty;
                RefreshState();
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Pairing failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        var address = ConnectAddressBox.Text.Trim();
        if (address.Length == 0)
        {
            ShowResult(
                false,
                "Enter the connection address.",
                null,
                "Use host:port from the Wireless debugging screen, such as 192.168.1.20:5555.");
            return;
        }

        SetBusy(true, $"Connecting to {address}...");
        try
        {
            var result = await _wireless.ConnectAsync(address, remember: true);
            ShowResult(result);
            RefreshState();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Connection failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    {
        var address = ConnectAddressBox.Text.Trim();
        if (address.Length == 0 && RememberedList.SelectedItem is DeviceRow selected)
        {
            address = selected.Address;
        }

        if (address.Length == 0)
        {
            ShowResult(false, "Enter or select an address to disconnect.", null, null);
            return;
        }

        SetBusy(true, $"Disconnecting from {address}...");
        try
        {
            ShowResult(await _wireless.DisconnectAsync(address, forget: false));
            RefreshState();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Disconnect failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnReconnectDeviceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row)
        {
            return;
        }

        SetBusy(true, $"Reconnecting {row.Address}...");
        try
        {
            ShowResult(await _wireless.ConnectAsync(row.Address, row.Device.Name, remember: false));
            RefreshState();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Reconnect failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnForgetDeviceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceRow row)
        {
            return;
        }

        if (!await ConfirmAsync(
                "Forget wireless device?",
                $"Remove {row.Title} ({row.Address}) from remembered devices? " +
                "You can pair it again from the device's Wireless debugging screen."))
        {
            return;
        }

        SetBusy(true, $"Forgetting {row.Address}...");
        try
        {
            ShowResult(await _wireless.DisconnectAsync(row.Address, forget: true));
            RefreshState();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Forget failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnUseServiceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServiceRow row)
        {
            return;
        }

        if (row.Kind == WirelessServiceKind.Pairing)
        {
            PairAddressBox.Text = row.Address;
            ShowGuidance(
                $"Pairing address {row.Address} copied to the manual pairing form. Enter the six-digit code shown on the device.");
        }
        else
        {
            ConnectAddressBox.Text = row.Address;
            ShowGuidance(
                $"Connection address {row.Address} copied. Press Connect, or use Pair manually first if the device is not paired.");
        }
    }

    private async void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || _disposed)
        {
            return;
        }

        var enabled = AutoReconnectToggle.IsOn;
        SetBusy(true, enabled ? "Starting automatic reconnect..." : "Stopping automatic reconnect...");
        try
        {
            if (enabled)
            {
                await _configuration.SetAsync(SettingsPaths.WirelessAutoReconnect, true);
                await _wireless.StartAsync();
            }
            else
            {
                await _wireless.StopAsync();
                await _configuration.SetAsync(SettingsPaths.WirelessAutoReconnect, false);
            }

            ShowResult(
                true,
                enabled
                    ? "Automatic reconnect is on. Remembered devices reconnect in the background."
                    : "Automatic reconnect is off. Remembered devices stay disconnected until you reconnect.",
                null,
                null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Automatic reconnect could not be changed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private void ShowResult(WirelessOperationResult result) =>
        ShowResult(result.Succeeded, result.Message, result.CommandLine, result.Guidance);

    private void ShowResult(bool succeeded, string message, string? commandLine, string? guidance)
    {
        ActionBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = commandLine is { Length: > 0 } command
            ? $"{message} Command: {command}"
            : message;
        ActionBar.IsOpen = true;
        if (guidance is { Length: > 0 })
        {
            ShowGuidance(guidance);
        }
    }

    private void ShowGuidance(string? guidance)
    {
        GuidanceBar.Message = guidance ?? string.Empty;
        GuidanceBar.IsOpen = guidance is { Length: > 0 };
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "Forget",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null)
        {
            PairingStatusText.Text = status;
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

    /// <summary>One discovered mDNS service shaped for the list.</summary>
    public sealed class ServiceRow
    {
        /// <summary>Creates a row.</summary>
        public ServiceRow(WirelessMdnsService service)
        {
            Service = service ?? throw new ArgumentNullException(nameof(service));
            UseId = "UseWirelessService_" + Sanitize(service.Address);
        }

        /// <summary>Gets the service.</summary>
        public WirelessMdnsService Service { get; }

        /// <summary>Gets the service name.</summary>
        public string Name => Service.Name;

        /// <summary>Gets the address.</summary>
        public string Address => Service.Address;

        /// <summary>Gets the kind and state label.</summary>
        public string Detail =>
            $"{(Service.Kind == WirelessServiceKind.Pairing ? "Pairing" : "Connect")} · {Service.StateLabel}";

        /// <summary>Gets the service kind.</summary>
        public WirelessServiceKind Kind => Service.Kind;

        /// <summary>Gets the Use button automation id.</summary>
        public string UseId { get; }

        private static string Sanitize(string value) =>
            string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_'));
    }

    /// <summary>One remembered wireless device shaped for the list.</summary>
    public sealed class DeviceRow
    {
        /// <summary>Creates a row.</summary>
        public DeviceRow(WirelessDevice device)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
            ReconnectId = "ReconnectWireless_" + Sanitize(device.Address);
            ForgetId = "ForgetWireless_" + Sanitize(device.Address);
        }

        /// <summary>Gets the remembered device.</summary>
        public WirelessDevice Device { get; }

        /// <summary>Gets the display title.</summary>
        public string Title => Device.Name is { Length: > 0 } name ? name : Device.Address;

        /// <summary>Gets the address.</summary>
        public string Address => Device.Address;

        /// <summary>Gets the reconnect detail line.</summary>
        public string Detail
        {
            get
            {
                var last = Device.LastConnectedAt is { } connected
                    ? $"last connected {connected.ToLocalTime():MMM d, HH:mm}"
                    : "not connected yet";
                return Device.AutoReconnect
                    ? $"{last} · auto-reconnect on"
                    : $"{last} · auto-reconnect off";
            }
        }

        /// <summary>Gets the Reconnect button automation id.</summary>
        public string ReconnectId { get; }

        /// <summary>Gets the Forget button automation id.</summary>
        public string ForgetId { get; }

        private static string Sanitize(string value) =>
            string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_'));
    }
}
