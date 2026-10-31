using System.Collections.ObjectModel;
using System.Globalization;
using DroidDeck.App.Services;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DroidDeck.App.Views;

/// <summary>Runs device toolbox actions: apps, permissions, shell, ports, files, intents, and plugins.</summary>
public sealed partial class DeviceToolsPage : Page, IRefreshablePage, IDisposable
{
    private static readonly string[] AppFilterLabels = ["User apps", "System apps", "All apps"];

    private readonly IDeviceService _devices;
    private readonly IDeviceToolboxService _toolbox;
    private readonly IDeviceFileService _files;
    private readonly IDeviceIntentService _intents;
    private readonly SessionWorkspace _workspace;
    private readonly PluginHost _plugins;
    private readonly IShellDialogs _dialogs;
    private readonly ILogger<DeviceToolsPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly UiOutputBuffer? _shellOutput;
    private readonly ObservableCollection<AppRow> _apps = [];
    private readonly ObservableCollection<PortRow> _ports = [];
    private readonly ObservableCollection<FileRow> _fileRows = [];
    private readonly ObservableCollection<ShellHistoryRow> _shellHistory = [];
    private readonly ObservableCollection<ExtraRow> _intentExtras = [];
    private readonly ObservableCollection<ExtraRow> _broadcastExtras = [];
    private readonly List<object> _pluginTabContents = [];
    private CancellationTokenSource? _appsCts;
    private CancellationTokenSource? _permissionCts;
    private CancellationTokenSource? _shellCts;
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _filesCts;
    private AppRow? _selectedApp;
    private PortRow? _selectedPort;
    private FileRow? _selectedFile;
    private string _filePath = "/sdcard";
    private bool _syncingDevice;
    private bool _busy;
    private bool _disposed;

    /// <summary>Creates the device tools page.</summary>
    public DeviceToolsPage(
        IDeviceService devices,
        IDeviceToolboxService toolbox,
        IDeviceFileService files,
        IDeviceIntentService intents,
        SessionWorkspace workspace,
        PluginHost plugins,
        IShellDialogs dialogs,
        ILogger<DeviceToolsPage> logger)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _intents = intents ?? throw new ArgumentNullException(nameof(intents));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _shellOutput = _dispatcher is null ? null : new UiOutputBuffer(_dispatcher, AppendShellText);

        InitializeComponent();
        AppFilterCombo.ItemsSource = AppFilterLabels;
        AppFilterCombo.SelectedIndex = 0;
        PortDirectionCombo.SelectedIndex = 0;
        AppList.ItemsSource = _apps;
        PermissionList.ItemsSource = Array.Empty<PermissionState>();
        PortList.ItemsSource = _ports;
        FileList.ItemsSource = _fileRows;
        ShellHistoryList.ItemsSource = _shellHistory;
        IntentExtrasList.ItemsSource = _intentExtras;
        BroadcastExtrasList.ItemsSource = _broadcastExtras;
        AddDefaultExtras();
        _devices.DevicesChanged += OnDevicesChanged;
        _plugins.Registry.Changed += OnPluginRegistryChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        RefreshDevices();
        RefreshShellHistory();
        RefreshPluginActions();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _devices.DevicesChanged -= OnDevicesChanged;
        _plugins.Registry.Changed -= OnPluginRegistryChanged;
        _appsCts?.Cancel();
        _permissionCts?.Cancel();
        _shellCts?.Cancel();
        _transferCts?.Cancel();
        _filesCts?.Cancel();
        DisposePluginTabs();
        _appsCts?.Dispose();
        _permissionCts?.Dispose();
        _shellCts?.Dispose();
        _transferCts?.Dispose();
        _filesCts?.Dispose();
    }

    private void AddDefaultExtras()
    {
        _intentExtras.Add(new ExtraRow());
        _broadcastExtras.Add(new ExtraRow());
    }

    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private string? SelectedSerial => SelectedDevice?.Serial;

    private void RefreshDevices()
    {
        var target = DeviceTargetSelection.Resolve(
            _devices.Devices,
            SelectedSerial,
            _workspace.DeviceSerial);
        _syncingDevice = true;
        try
        {
            DeviceCombo.ItemsSource = target.Usable;
            DeviceCombo.SelectedItem = target.Selected;
        }
        finally
        {
            _syncingDevice = false;
        }

        AvailabilityBar.Message = target.Usable.Count == 0
            ? _devices.LastError
              ?? "Connect a device with USB debugging enabled and authorize this computer. Emulators appear here too."
            : string.Empty;
        AvailabilityBar.IsOpen = target.Usable.Count == 0;
        if (target.Usable.Count > 0)
        {
            OnDeviceChanged();
        }
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(RefreshDevices);

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingDevice)
        {
            OnDeviceChanged();
        }
    }

    private void OnDeviceChanged()
    {
        _appsCts?.Cancel();
        _permissionCts?.Cancel();
        _filesCts?.Cancel();
        _selectedApp = null;
        _selectedPort = null;
        _selectedFile = null;
        _apps.Clear();
        _ports.Clear();
        _fileRows.Clear();
        PermissionList.ItemsSource = Array.Empty<PermissionState>();
        UpdateAppSelection();
        UpdatePermissionHint();
        UpdatePortSelection();
        UpdateFileSelection();
        if (SelectedDevice is { } device)
        {
            _workspace.OpenDevice(device.Serial);
            _ = LoadAppsAsync();
            _ = LoadPortsAsync();
            _ = LoadFilesAsync(_filePath);
            RebuildPluginTabs();
        }
        else
        {
            RebuildPluginTabs();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshDevices();
        if (SelectedDevice is null)
        {
            return;
        }

        await LoadAppsAsync();
        await LoadPortsAsync();
        await LoadFilesAsync(_filePath);
    }

    private AppListFilter CurrentFilter => AppFilterCombo.SelectedIndex switch
    {
        1 => AppListFilter.System,
        2 => AppListFilter.All,
        _ => AppListFilter.User,
    };

    private async Task LoadAppsAsync()
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        _appsCts?.Cancel();
        _appsCts?.Dispose();
        _appsCts = new CancellationTokenSource();
        var token = _appsCts.Token;
        SetBusy(true);
        try
        {
            var apps = await _toolbox.ListAppsAsync(serial, CurrentFilter, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _apps.Clear();
            foreach (var app in apps.OrderBy(app => app.Label, StringComparer.OrdinalIgnoreCase))
            {
                _apps.Add(new AppRow(app));
            }

            AppEmptyText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PermissionAppCombo.ItemsSource = _apps;
            PermissionAppCombo.SelectedItem = _apps.FirstOrDefault();
            UpdateAppSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Applications could not be listed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnLoadAppsClick(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async void OnAppFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && SelectedSerial is not null)
        {
            await LoadAppsAsync();
        }
    }

    private void OnAppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedApp = AppList.SelectedItem as AppRow;
        UpdateAppSelection();
    }

    private void UpdateAppSelection()
    {
        if (_selectedApp is not { } row)
        {
            AppSelectedName.Text = "Select an app";
            AppSelectedPackage.Text = string.Empty;
            AppSelectedDetails.Text = string.Empty;
            foreach (var button in new[] { AppInfoButton, LoadPermissionsButton, ForceStopButton, ClearDataButton, UninstallButton })
            {
                button.IsEnabled = false;
            }

            return;
        }

        AppSelectedName.Text = row.App.Label;
        AppSelectedPackage.Text = row.App.PackageName;
        AppSelectedDetails.Text = row.App.VersionName is { Length: > 0 } version
            ? $"{row.App.KindLabel} app · version {version}"
            : $"{row.App.KindLabel} app";
        var enabled = SelectedDevice is not null && !_busy;
        AppInfoButton.IsEnabled = enabled;
        LoadPermissionsButton.IsEnabled = enabled;
        ForceStopButton.IsEnabled = enabled;
        ClearDataButton.IsEnabled = enabled;
        UninstallButton.IsEnabled = enabled;
    }

    private async void OnShowPermissionsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedApp is null)
        {
            return;
        }

        PermissionAppCombo.SelectedItem = _selectedApp;
        MainTabs.SelectedIndex = 1;
        await LoadPermissionsAsync();
    }

    private async Task LoadPermissionsAsync()
    {
        if (SelectedSerial is not { } serial || PermissionAppCombo.SelectedItem is not AppRow row)
        {
            return;
        }

        _permissionCts?.Cancel();
        _permissionCts?.Dispose();
        _permissionCts = new CancellationTokenSource();
        var token = _permissionCts.Token;
        SetBusy(true);
        try
        {
            var permissions = (await _toolbox.ListPermissionsAsync(serial, row.App.PackageName, token))
                .Where(static permission => permission.IsRuntime)
                .ToArray();
            if (token.IsCancellationRequested)
            {
                return;
            }

            PermissionList.ItemsSource = permissions;
            PermissionEmptyText.Visibility = permissions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            PermissionHintText.Text = permissions.Length == 0
                ? $"No runtime permissions were reported for {row.App.PackageName}. The app may not declare dangerous permissions."
                : $"{permissions.Length} runtime permission(s) for {row.App.PackageName}. Grant or revoke is applied to this device only.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Permissions could not be read: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdatePermissionHint()
    {
        PermissionHintText.Text = SelectedDevice is null
            ? "Select a device first."
            : "Select an app to list its runtime permissions.";
    }

    private async void OnPermissionAppChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await LoadPermissionsAsync();
        }
    }

    private async void OnReloadPermissionsClick(object sender, RoutedEventArgs e) => await LoadPermissionsAsync();

    private async void OnGrantPermissionClick(object sender, RoutedEventArgs e) =>
        await SetPermissionAsync(sender, grant: true);

    private async void OnRevokePermissionClick(object sender, RoutedEventArgs e) =>
        await SetPermissionAsync(sender, grant: false);

    private async Task SetPermissionAsync(object sender, bool grant)
    {
        if (SelectedSerial is not { } serial ||
            PermissionAppCombo.SelectedItem is not AppRow app ||
            (sender as FrameworkElement)?.DataContext is not PermissionState permission)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _toolbox.SetPermissionAsync(serial, app.App.PackageName, permission.Name, grant);
            ShowResult(result);
            await LoadPermissionsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The permission could not be changed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnOpenAppInfoClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial || _selectedApp is not { } app)
        {
            return;
        }

        await RunToolboxAsync(() => _toolbox.OpenAppInfoAsync(serial, app.App.PackageName));
    }

    private async void OnForceStopClick(object sender, RoutedEventArgs e) =>
        await RunAppActionAsync("Force stop", "Force stop {0}? Unsaved state in the app is lost.", (serial, package) =>
            _toolbox.ForceStopAppAsync(serial, package, confirmed: true));

    private async void OnClearDataClick(object sender, RoutedEventArgs e) =>
        await RunAppActionAsync("Clear app data", "Clear all data for {0}? This cannot be undone.", (serial, package) =>
            _toolbox.ClearAppDataAsync(serial, package, confirmed: true));

    private async void OnUninstallClick(object sender, RoutedEventArgs e) =>
        await RunAppActionAsync("Uninstall", "Uninstall {0}? The app and its data are removed.", (serial, package) =>
            _toolbox.UninstallAppAsync(serial, package, confirmed: true));

    private async Task RunAppActionAsync(
        string title,
        string confirmation,
        Func<string, string, Task<ToolboxCommandResult>> action)
    {
        if (SelectedSerial is not { } serial || _selectedApp is not { } app)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(title, string.Format(CultureInfo.InvariantCulture, confirmation, app.App.PackageName)))
        {
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await action(serial, app.App.PackageName));
            await LoadAppsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"{title} failed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunToolboxAsync(Func<Task<ToolboxCommandResult>> action)
    {
        SetBusy(true);
        try
        {
            ShowResult(await action());
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The action failed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRunShellClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        var command = ShellCommandBox.Text.Trim();
        if (command.Length == 0)
        {
            ShowResult(false, "Enter a shell command first.", null, string.Empty);
            return;
        }

        _shellCts?.Cancel();
        _shellCts?.Dispose();
        _shellCts = new CancellationTokenSource();
        var token = _shellCts.Token;
        ShellRunButton.IsEnabled = false;
        ShellCancelButton.IsEnabled = true;
        _shellOutput?.Reset();
        ShellOutput.Text = string.Empty;
        try
        {
            var progress = new ActionProgress<ProcessOutputLine>(line => _shellOutput?.Append(line.Text));
            var result = await _toolbox.RunShellAsync(serial, command, output: progress, cancellationToken: token);
            if (result.Blocked && result.IsDestructive)
            {
                if (await _dialogs.ConfirmAsync(
                        "Run state-changing command?",
                        $"This command changes device state:\n\n{command}\n\nRun it anyway?"))
                {
                    result = await _toolbox.RunShellAsync(
                        serial,
                        command,
                        allowDestructive: true,
                        output: progress,
                        cancellationToken: token);
                }
            }

            _shellOutput?.Reset();
            ShellOutput.Text = result.Output;
            ShellCommandLineText.Text = result.CommandLine ?? string.Empty;
            ShowResult(result, expand: false);
            RefreshShellHistory();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ShellOutput.Text = "The command was cancelled.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The shell command failed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            ShellRunButton.IsEnabled = true;
            ShellCancelButton.IsEnabled = false;
        }
    }

    private void AppendShellText(string text)
    {
        ShellOutput.Text += text;
        if (ShellOutput.Text.Length > 200_000)
        {
            ShellOutput.Text = ShellOutput.Text[^200_000..];
        }
    }

    private void OnCancelShellClick(object sender, RoutedEventArgs e) => _shellCts?.Cancel();

    private void RefreshShellHistory()
    {
        _shellHistory.Clear();
        foreach (var entry in _toolbox.ShellHistory)
        {
            _shellHistory.Add(new ShellHistoryRow(entry));
        }
    }

    private void OnShellHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShellHistoryList.SelectedItem is ShellHistoryRow row)
        {
            ShellCommandBox.Text = row.Entry.Command;
            ShellOutput.Text = row.Entry.Output;
            ShellCommandLineText.Text = row.Entry.CommandLine ?? string.Empty;
        }
    }

    private void OnClearShellHistoryClick(object sender, RoutedEventArgs e)
    {
        _toolbox.ClearShellHistory();
        RefreshShellHistory();
        ShowResult(true, "Shell history cleared.", null, string.Empty);
    }

    private PortDirection CurrentPortDirection => PortDirectionCombo.SelectedIndex == 1
        ? PortDirection.Reverse
        : PortDirection.Forward;

    private async Task LoadPortsAsync()
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        try
        {
            var ports = await _toolbox.ListPortsAsync(serial, CurrentPortDirection);
            _ports.Clear();
            foreach (var mapping in ports)
            {
                _ports.Add(new PortRow(mapping));
            }

            PortEmptyText.Visibility = _ports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePortSelection();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Port mappings could not be listed: {exception.Message}", null, string.Empty);
        }
    }

    private async void OnRefreshPortsClick(object sender, RoutedEventArgs e) => await LoadPortsAsync();

    private async void OnPortDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await LoadPortsAsync();
        }
    }

    private void OnPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPort = PortList.SelectedItem as PortRow;
        UpdatePortSelection();
    }

    private void UpdatePortSelection()
    {
        RemovePortButton.IsEnabled = _selectedPort is not null;
    }

    private async void OnAddPortClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        var local = PortLocalBox.Text.Trim();
        var remote = PortRemoteBox.Text.Trim();
        if (local.Length == 0 || remote.Length == 0)
        {
            ShowResult(false, "Enter both the local and remote socket specifications.", null, string.Empty);
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await _toolbox.AddPortAsync(serial, CurrentPortDirection, local, remote));
            await LoadPortsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The port mapping could not be added: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRemovePortClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial || _selectedPort is not { } port)
        {
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await _toolbox.RemovePortAsync(
                serial,
                port.Mapping.Direction,
                port.Mapping.Local,
                port.Mapping.Remote));
            await LoadPortsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The port mapping could not be removed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRemoveAllPortsClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "Remove every port mapping?",
                $"Remove every {CurrentPortDirection.ToString().ToLowerInvariant()} mapping for {serial}?"))
        {
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await _toolbox.RemoveAllPortsAsync(serial, CurrentPortDirection, confirmed: true));
            await LoadPortsAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The port mappings could not be removed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadFilesAsync(string path)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        _filesCts?.Cancel();
        _filesCts?.Dispose();
        _filesCts = new CancellationTokenSource();
        var token = _filesCts.Token;
        SetBusy(true);
        try
        {
            var entries = await _files.ListAsync(serial, path, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _filePath = path;
            FilePathBox.Text = path;
            _fileRows.Clear();
            foreach (var entry in entries
                         .OrderByDescending(entry => entry.IsDirectory)
                         .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                _fileRows.Add(new FileRow(entry));
            }

            FileEmptyText.Visibility = _fileRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateFileSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The folder could not be listed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnGoToPathClick(object sender, RoutedEventArgs e) =>
        await LoadFilesAsync(NormalizeDevicePath(FilePathBox.Text));

    private async void OnGoUpClick(object sender, RoutedEventArgs e) =>
        await LoadFilesAsync(ParentDevicePath(_filePath));

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedFile = FileList.SelectedItem as FileRow;
        UpdateFileSelection();
    }

    private void UpdateFileSelection()
    {
        var idle = !_busy && _transferCts is null;
        PushFileButton.IsEnabled = idle;
        var enabled = _selectedFile is not null && idle;
        PullFileButton.IsEnabled = enabled;
        DeleteFileButton.IsEnabled = enabled;
    }

    private async void OnOpenFileEntryClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FileRow { Entry.IsDirectory: true } row)
        {
            await LoadFilesAsync(row.Entry.FullPath);
        }
    }

    private async void OnPushFileClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        _dialogs.InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await RunTransferAsync(
            $"Pushing {file.Name}",
            (progress, token) => _files.PushAsync(
                serial,
                file.Path,
                CombineDevicePath(_filePath, file.Name),
                progress,
                token),
            reloadFiles: true);
    }

    private async void OnPullFileClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial || _selectedFile is not { } file)
        {
            return;
        }

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
        };
        picker.FileTypeFilter.Add("*");
        _dialogs.InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var localPath = System.IO.Path.Combine(folder.Path, file.Entry.Name);
        await RunTransferAsync(
            $"Pulling {file.Entry.Name}",
            (progress, token) => _files.PullAsync(
                serial,
                file.Entry.FullPath,
                localPath,
                progress,
                token),
            reloadFiles: false);
    }

    private async Task RunTransferAsync(
        string title,
        Func<IProgress<FileTransferProgress>, CancellationToken, Task<ToolboxCommandResult>> transfer,
        bool reloadFiles)
    {
        if (_transferCts is not null)
        {
            return;
        }

        var transferCts = new CancellationTokenSource();
        _transferCts = transferCts;
        var token = transferCts.Token;
        FileProgress.IsIndeterminate = true;
        FileProgress.Value = 0;
        FileStatusText.Text = title;
        CancelTransferButton.IsEnabled = true;
        SetBusy(true);
        try
        {
            var progress = new Progress<FileTransferProgress>(report =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    FileStatusText.Text = report.Detail is { Length: > 0 }
                        ? $"{report.Phase}: {report.Detail}"
                        : report.Phase;
                    if (report.Percent is { } percent)
                    {
                        FileProgress.IsIndeterminate = false;
                        FileProgress.Value = Math.Clamp(percent, 0, 100);
                    }
                }));
            var result = await transfer(progress, token);
            ShowResult(result);
            if (reloadFiles)
            {
                await LoadFilesAsync(_filePath);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ShowResult(false, "The transfer was cancelled.", null, string.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"{title} failed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            CancelTransferButton.IsEnabled = false;
            FileProgress.IsIndeterminate = false;
            FileStatusText.Text = string.Empty;
            _transferCts = null;
            transferCts.Dispose();
            SetBusy(false);
            UpdateFileSelection();
        }
    }

    private void OnCancelTransferClick(object sender, RoutedEventArgs e) => _transferCts?.Cancel();

    private async void OnDeleteFileClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial || _selectedFile is not { } file)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "Delete device file?",
                $"Delete {(file.Entry.IsDirectory ? "the folder" : "the file")} {file.Entry.FullPath}? This cannot be undone."))
        {
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await _files.DeleteAsync(
                serial,
                file.Entry.FullPath,
                file.Entry.IsDirectory,
                confirmed: true));
            await LoadFilesAsync(_filePath);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The entry could not be deleted: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnAddIntentExtraClick(object sender, RoutedEventArgs e) => _intentExtras.Add(new ExtraRow());

    private void OnAddBroadcastExtraClick(object sender, RoutedEventArgs e) => _broadcastExtras.Add(new ExtraRow());

    private void OnRemoveExtraClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExtraRow row)
        {
            return;
        }

        if (!_intentExtras.Remove(row))
        {
            _broadcastExtras.Remove(row);
        }
    }

    private async void OnSendIntentClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        if (!TryParseFlags(IntentFlagsBox.Text, out var flags))
        {
            ShowResult(false, "Intent flags must be a decimal or hexadecimal integer.", null, string.Empty);
            return;
        }

        var request = new DeviceIntentRequest
        {
            Action = EmptyToNull(IntentActionBox.Text),
            Data = EmptyToNull(IntentDataBox.Text),
            MimeType = EmptyToNull(IntentMimeBox.Text),
            Package = EmptyToNull(IntentPackageBox.Text),
            Component = EmptyToNull(IntentComponentBox.Text),
            Categories = SplitList(IntentCategoriesBox.Text),
            Extras = BuildExtras(_intentExtras),
            Flags = flags,
            WaitForResult = IntentWaitCheck.IsChecked == true,
        };
        if (request.Action is null && request.Data is null && request.Component is null && request.Package is null)
        {
            ShowResult(false, "Provide an action, data URI, component, or package.", null, string.Empty);
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await _intents.SendIntentAsync(serial, request));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The intent could not be sent: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSendBroadcastClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        var action = BroadcastActionBox.Text.Trim();
        if (action.Length == 0)
        {
            ShowResult(false, "Enter a broadcast action first.", null, string.Empty);
            return;
        }

        var request = new DeviceBroadcastRequest
        {
            Action = action,
            Package = EmptyToNull(BroadcastPackageBox.Text),
            Component = EmptyToNull(BroadcastComponentBox.Text),
            Extras = BuildExtras(_broadcastExtras),
            IncludeStoppedPackages = BroadcastStoppedCheck.IsChecked == true,
        };
        SetBusy(true);
        try
        {
            ShowResult(await _intents.SendBroadcastAsync(serial, request));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The broadcast could not be sent: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRebootSystemClick(object sender, RoutedEventArgs e) => await RebootAsync(RebootMode.System);

    private async void OnRebootRecoveryClick(object sender, RoutedEventArgs e) => await RebootAsync(RebootMode.Recovery);

    private async void OnRebootBootloaderClick(object sender, RoutedEventArgs e) => await RebootAsync(RebootMode.Bootloader);

    private async Task RebootAsync(RebootMode mode)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        var target = mode switch
        {
            RebootMode.Recovery => "recovery",
            RebootMode.Bootloader => "bootloader",
            _ => "system",
        };
        if (!await _dialogs.ConfirmAsync(
                $"Reboot to {target}?",
                $"Reboot {serial} into {target}? The device disconnects immediately, and any unsaved state is lost."))
        {
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(await _intents.RebootAsync(serial, mode, confirmed: true));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The reboot failed: {exception.Message}", null, string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnOpenPerformanceClick(object sender, RoutedEventArgs e) => OpenTool("devices/performance");

    private void OnOpenInspectorClick(object sender, RoutedEventArgs e) => OpenTool("devices/inspector");

    private void OpenTool(string key)
    {
        if (SelectedDevice is { } device)
        {
            _workspace.OpenDevice(device.Serial);
        }

        App.Instance.Navigate(key);
    }

    private void RefreshPluginActions()
    {
        var rows = _plugins.Extensions.DeviceActions
            .Select(entry => new PluginActionRow(entry, _plugins.Health.Get(entry.PluginId)))
            .ToArray();
        PluginActionList.ItemsSource = rows;
        PluginEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPluginRegistryChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        RefreshPluginActions();
        RebuildPluginTabs();
    });

    private async void OnRunPluginActionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginActionRow row ||
            SelectedDevice is not { } device)
        {
            return;
        }

        var context = new DeviceActionContext(device.Serial, device.Model);
        if (row.Entry.Action.RequiresConfirmation &&
            !await _dialogs.ConfirmAsync(
                "Run plugin action?",
                $"{row.Entry.Action.Name} from {row.SourceLabel} changes device state. Run it on {device.DisplayName}?"))
        {
            return;
        }

        var result = await _plugins.Invoker.InvokeDeviceActionAsync(
            row.Entry.PluginId,
            row.Entry.Action,
            context);
        RefreshPluginActions();
        if (result.Succeeded)
        {
            ShowResult(true, $"Ran {row.Entry.Action.Name} from {row.SourceLabel} ({result.Duration.TotalMilliseconds:F0} ms).", null, string.Empty);
        }
        else
        {
            ShowResult(
                false,
                $"{row.Entry.Action.Name} from {row.SourceLabel} failed: {result.Error}",
                null,
                string.Empty);
        }
    }

    private void RebuildPluginTabs()
    {
        DisposePluginTabs();
        PluginTabs.TabItems.Clear();
        if (SelectedDevice is not { } device)
        {
            PluginTabEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var context = new DeviceTabContext(device.Serial, device.Model);
        foreach (var entry in _plugins.Extensions.DeviceTabs)
        {
            var created = _plugins.Invoker.CreateTabContent(entry.PluginId, entry.Tab, context);
            var item = new TabViewItem
            {
                Header = $"{entry.Tab.Title} · {entry.SourceLabel}",
                IsClosable = false,
            };
            if (created.Content is FrameworkElement element)
            {
                item.Content = element;
                _pluginTabContents.Add(element);
            }
            else if (created.Content is not null)
            {
                item.Content = new TextBlock
                {
                    Text = created.Content.ToString(),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12),
                };
                _pluginTabContents.Add(created.Content);
            }
            else
            {
                item.Content = new TextBlock
                {
                    Text = $"The tab could not be created: {created.Error}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ResolveBrush("SystemFillColorCriticalBrush"),
                    Margin = new Thickness(12),
                };
            }

            PluginTabs.TabItems.Add(item);
        }

        PluginTabEmptyText.Visibility = PluginTabs.TabItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DisposePluginTabs()
    {
        foreach (var content in _pluginTabContents)
        {
            if (content is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _pluginTabContents.Clear();
        PluginTabs.TabItems.Clear();
    }

    private static Brush ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private void ShowResult(ToolboxCommandResult result, bool expand = true) =>
        ShowResult(result.Succeeded, result.Message, result.CommandLine, result.Output, expand);

    private void ShowResult(bool succeeded, string message, string? commandLine, string output, bool expand = true)
    {
        ResultBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
        ResultCommandText.Text = commandLine is { Length: > 0 } ? $"Command: {commandLine}" : string.Empty;
        ResultOutputText.Text = output;
        if (expand && (output.Length > 0 || commandLine is { Length: > 0 }))
        {
            OutputExpander.IsExpanded = true;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppSelection();
        UpdateFileSelection();
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

    private static string NormalizeDevicePath(string? path)
    {
        var value = string.IsNullOrWhiteSpace(path) ? "/sdcard" : path.Trim();
        return value.StartsWith('/') ? value : "/" + value;
    }

    private static string ParentDevicePath(string path)
    {
        var normalized = path.TrimEnd('/');
        if (normalized.Length == 0)
        {
            return "/";
        }

        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    private static string CombineDevicePath(string directory, string name) =>
        directory.TrimEnd('/') + "/" + name.TrimStart('/');

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static List<IntentExtra> BuildExtras(IEnumerable<ExtraRow> rows)
    {
        var extras = new List<IntentExtra>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Key))
            {
                continue;
            }

            extras.Add(new IntentExtra(row.Key.Trim(), row.Type, row.Value ?? string.Empty));
        }

        return extras;
    }

    private static bool TryParseFlags(string? text, out int? flags)
    {
        flags = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        try
        {
            flags = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(value, 16)
                : int.Parse(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>One installed application shaped for the list and picker.</summary>
    public sealed class AppRow
    {
        /// <summary>Creates a row.</summary>
        public AppRow(ToolboxApp app)
        {
            App = app ?? throw new ArgumentNullException(nameof(app));
        }

        /// <summary>Gets the application.</summary>
        public ToolboxApp App { get; }

        /// <summary>Gets the display label.</summary>
        public string Label => App.Label;

        /// <summary>Gets the package name.</summary>
        public string PackageName => App.PackageName;

        /// <summary>Gets the install kind.</summary>
        public string KindLabel => App.KindLabel;

        /// <summary>Gets the label and package for a picker.</summary>
        public string Display => $"{Label} ({PackageName})";
    }

    /// <summary>One extra being edited.</summary>
    public sealed class ExtraRow
    {
        /// <summary>Gets the available extra types.</summary>
        public IReadOnlyList<IntentExtraType> Types { get; } = Enum.GetValues<IntentExtraType>();

        /// <summary>Gets or sets the extra key.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Gets or sets the extra type.</summary>
        public IntentExtraType Type { get; set; } = IntentExtraType.Text;

        /// <summary>Gets or sets the extra value.</summary>
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>One port mapping shaped for the list.</summary>
    public sealed class PortRow
    {
        /// <summary>Creates a row.</summary>
        public PortRow(PortMapping mapping)
        {
            Mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        /// <summary>Gets the mapping.</summary>
        public PortMapping Mapping { get; }

        /// <summary>Gets the display label.</summary>
        public string Display => Mapping.Display;

        /// <summary>Gets the direction label.</summary>
        public string DirectionLabel => Mapping.Direction == PortDirection.Forward
            ? "Host to device"
            : "Device to host";
    }

    /// <summary>One device file shaped for the list.</summary>
    public sealed class FileRow
    {
        /// <summary>Creates a row.</summary>
        public FileRow(DeviceFileEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        /// <summary>Gets the file entry.</summary>
        public DeviceFileEntry Entry { get; }

        /// <summary>Gets the name.</summary>
        public string Name => Entry.Name;

        /// <summary>Gets the size label.</summary>
        public string SizeLabel => Entry.SizeLabel;

        /// <summary>Gets whether this is a directory.</summary>
        public bool IsDirectory => Entry.IsDirectory;

        /// <summary>Gets the icon glyph.</summary>
        public string Glyph => Entry.IsDirectory ? "\uE8B7" : "\uE8A5";
    }

    /// <summary>One shell history entry shaped for the list.</summary>
    public sealed class ShellHistoryRow
    {
        /// <summary>Creates a row.</summary>
        public ShellHistoryRow(ShellHistoryEntry entry)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        /// <summary>Gets the history entry.</summary>
        public ShellHistoryEntry Entry { get; }

        /// <summary>Gets the command.</summary>
        public string Command => Entry.Command;

        /// <summary>Gets the result summary.</summary>
        public string Detail =>
            $"{(Entry.Succeeded ? "OK" : "Failed")} · {Entry.Timestamp.ToLocalTime():HH:mm:ss}";
    }

    /// <summary>One plugin device action shaped for the list.</summary>
    public sealed class PluginActionRow
    {
        /// <summary>Creates a row.</summary>
        public PluginActionRow(DeviceActionEntry entry, PluginHealthSnapshot health)
        {
            Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Health = health ?? throw new ArgumentNullException(nameof(health));
            RunId = $"RunPluginAction_{entry.Action.Id}";
        }

        /// <summary>Gets the action entry.</summary>
        public DeviceActionEntry Entry { get; }

        /// <summary>Gets the latest health snapshot.</summary>
        public PluginHealthSnapshot Health { get; }

        /// <summary>Gets the action.</summary>
        public IDeviceAction Action => Entry.Action;

        /// <summary>Gets the contributing plugin label.</summary>
        public string SourceLabel => Entry.SourceLabel;

        /// <summary>Gets the list subtitle.</summary>
        public string Subtitle => Health.IsHealthy
            ? $"{SourceLabel} · {Entry.Action.Id}"
            : $"{SourceLabel} · unhealthy: {Health.LastError}";

        /// <summary>Gets the run button automation id.</summary>
        public string RunId { get; }
    }
}
