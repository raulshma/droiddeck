using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using DroidDeck.App.Services;
using DroidDeck.Core.Emulators;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Views;

/// <summary>Lists and manages Android virtual devices with progress and confirmations.</summary>
public sealed partial class EmulatorsPage : Page, IRefreshablePage, IDisposable
{
    private const string AvdHelpUrl = "https://developer.android.com/studio/run/managing-avds";

    private readonly IEmulatorService _emulators;
    private readonly IShellDialogs _dialogs;
    private readonly DispatcherQueue? _dispatcher;
    private readonly ObservableCollection<AvdRow> _rows = [];
    private bool _busy;
    private bool _disposed;

    /// <summary>Creates the emulator page.</summary>
    public EmulatorsPage(IEmulatorService emulators, IShellDialogs dialogs)
    {
        _emulators = emulators ?? throw new ArgumentNullException(nameof(emulators));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        InitializeComponent();
        AvdList.ItemsSource = _rows;
        AvdList.SelectionChanged += OnSelectionChanged;
        _emulators.AvdsChanged += OnAvdsChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh() => _ = LoadAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _emulators.AvdsChanged -= OnAvdsChanged;
    }

    private async Task LoadAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Discovering AVDs...");
        try
        {
            await _emulators.ListAsync();
            ApplyRows();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowMessage($"AVDs could not be listed: {exception.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyRows()
    {
        var selected = (AvdList.SelectedItem as AvdRow)?.Avd.Name;
        _rows.Clear();
        foreach (var avd in _emulators.Avds)
        {
            _rows.Add(new AvdRow(avd));
        }

        AvdList.SelectedItem = _rows.FirstOrDefault(row =>
            string.Equals(row.Avd.Name, selected, StringComparison.OrdinalIgnoreCase)) ?? _rows.FirstOrDefault();
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AvailabilityBar.Message = _emulators.AvailabilityError ?? string.Empty;
        AvailabilityBar.IsOpen = _emulators.AvailabilityError is not null;
        UpdateSelection();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if ((AvdList.SelectedItem as AvdRow)?.Avd is not { } avd)
        {
            SelectedNameText.Text = "Select an AVD";
            SelectedDetailsText.Text = string.Empty;
            return;
        }

        SelectedNameText.Text = avd.DisplayName;
        SelectedDetailsText.Text = AvdRow.Describe(avd);
        if (SnapshotNameBox.Text.Length == 0 && avd.Snapshots.Count > 0)
        {
            SnapshotNameBox.Text = avd.Snapshots[0];
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnLaunchClick(object sender, RoutedEventArgs e) => await LaunchAsync(coldBoot: false);

    private async void OnColdBootClick(object sender, RoutedEventArgs e) => await LaunchAsync(coldBoot: true);

    private async Task LaunchAsync(bool coldBoot)
    {
        if ((AvdList.SelectedItem as AvdRow)?.Avd is not { } avd || _busy)
        {
            return;
        }

        SetBusy(true, coldBoot ? $"Cold booting {avd.Name}..." : $"Starting {avd.Name}...");
        try
        {
            var progress = new Progress<EmulatorProgress>(report =>
                DispatcherQueue.TryEnqueue(() => ShowBusyMessage(report.Message)));
            var result = await _emulators.LaunchAsync(
                avd.Name,
                new EmulatorLaunchOptions { ColdBoot = coldBoot },
                progress);
            ShowResult(result);
            ApplyRows();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowMessage($"Launching {avd.Name} failed: {exception.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnSaveSnapshotClick(object sender, RoutedEventArgs e) =>
        await RunSnapshotAsync(save: true);

    private async void OnLoadSnapshotClick(object sender, RoutedEventArgs e) =>
        await RunSnapshotAsync(save: false);

    private async Task RunSnapshotAsync(bool save)
    {
        if ((AvdList.SelectedItem as AvdRow)?.Avd is not { } avd || _busy)
        {
            return;
        }

        var name = SnapshotNameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowMessage("Enter a snapshot name first.", isError: true);
            return;
        }

        SetBusy(true, save ? $"Saving snapshot {name}..." : $"Loading snapshot {name}...");
        try
        {
            var result = save
                ? await _emulators.SaveSnapshotAsync(avd.Name, name)
                : await _emulators.LoadSnapshotAsync(avd.Name, name);
            ShowResult(result);
            ApplyRows();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowMessage($"The snapshot operation failed: {exception.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if ((AvdList.SelectedItem as AvdRow)?.Avd is not { } avd || _busy)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Stop emulator", $"Stop {avd.Name}? Unsaved app state is lost."))
        {
            return;
        }

        await RunOperationAsync($"Stopping {avd.Name}...", () => _emulators.StopAsync(avd.Name));
    }

    private async void OnWipeClick(object sender, RoutedEventArgs e)
    {
        if ((AvdList.SelectedItem as AvdRow)?.Avd is not { } avd || _busy)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                "Wipe emulator data",
                $"Wipe all user data and snapshots for {avd.Name}? This cannot be undone."))
        {
            return;
        }

        await RunOperationAsync($"Wiping {avd.Name}...", () => _emulators.WipeDataAsync(avd.Name));
    }

    private async Task RunOperationAsync(string status, Func<Task<EmulatorOperationResult>> operation)
    {
        SetBusy(true, status);
        try
        {
            ShowResult(await operation());
            ApplyRows();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowMessage($"The emulator operation failed: {exception.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowResult(EmulatorOperationResult result)
    {
        var message = result.Guidance is null
            ? result.Message
            : $"{result.Message} {result.Guidance}";
        if (result.CommandLine is { Length: > 0 } command)
        {
            message = $"{message} Command: {command}";
        }

        ShowMessage(message, isError: !result.Succeeded);
    }

    private void ShowMessage(string message, bool isError)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null)
        {
            ShowBusyMessage(status);
        }
    }

    private void ShowBusyMessage(string message)
    {
        ActionBar.Severity = InfoBarSeverity.Informational;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private void OnOpenDeviceManagerClick(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = AvdHelpUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            ShowMessage("The Android Studio help page could not be opened.", isError: true);
        }
    }

    private void OnAvdsChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            ApplyRows();
            return;
        }

        _dispatcher.TryEnqueue(ApplyRows);
    }

    /// <summary>One AVD shaped for the list.</summary>
    public sealed class AvdRow
    {
        /// <summary>Creates a row.</summary>
        public AvdRow(EmulatorAvdInfo avd)
        {
            Avd = avd ?? throw new ArgumentNullException(nameof(avd));
            StateLabel = avd.State switch
            {
                EmulatorAvdState.Running => "Running",
                EmulatorAvdState.Starting => "Starting",
                EmulatorAvdState.Unknown => "Unknown",
                _ => "Stopped",
            };
            StateBrush = ResolveBrush(avd.State switch
            {
                EmulatorAvdState.Running => "SystemFillColorSuccessBrush",
                EmulatorAvdState.Starting => "SystemFillColorCautionBrush",
                EmulatorAvdState.Unknown => "SystemFillColorCautionBrush",
                _ => "TextFillColorSecondaryBrush",
            });
            Details = Describe(avd);
        }

        /// <summary>Gets the AVD.</summary>
        public EmulatorAvdInfo Avd { get; }

        /// <summary>Gets the display name.</summary>
        public string DisplayName => Avd.DisplayName;

        /// <summary>Gets the AVD details.</summary>
        public string Details { get; }

        /// <summary>Gets the state label.</summary>
        public string StateLabel { get; }

        /// <summary>Gets the state brush.</summary>
        public Brush StateBrush { get; }

        /// <summary>Builds the one-line AVD description.</summary>
        public static string Describe(EmulatorAvdInfo avd)
        {
            ArgumentNullException.ThrowIfNull(avd);
            var parts = new List<string>();
            if (avd.Target is { Length: > 0 } target)
            {
                parts.Add(target);
            }

            if (avd.Abi is { Length: > 0 } abi)
            {
                parts.Add(abi);
            }

            if (avd.DeviceProfile is { Length: > 0 } profile)
            {
                parts.Add(profile);
            }

            if (avd.Serial is { Length: > 0 } serial)
            {
                parts.Add($"adb {serial}");
            }

            if (avd.Snapshots.Count > 0)
            {
                parts.Add($"snapshots: {string.Join(", ", avd.Snapshots)}");
            }

            return parts.Count == 0 ? avd.Name : string.Join(" · ", parts);
        }

        private static Brush ResolveBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
