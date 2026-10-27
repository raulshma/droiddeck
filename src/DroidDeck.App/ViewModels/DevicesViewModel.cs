using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DroidDeck.App.ViewModels;

/// <summary>
/// Keeps the device list and the selected-device overview in sync with the
/// device service, marshaling background updates onto the UI thread.
/// </summary>
public sealed class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly IDeviceService _devices;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ILogger<DevicesViewModel> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private DeviceRowViewModel? _selectedDevice;
    private bool _isBusy;
    private bool _hasConnectionProblem;
    private bool _hadDevices;
    private string _actionMessage = string.Empty;
    private string _errorMessage = string.Empty;
    private string _connectionProblemText = string.Empty;
    private bool _disposed;

    /// <summary>Creates the devices view model.</summary>
    public DevicesViewModel(
        IDeviceService devices,
        IDiagnosticsService diagnostics,
        ILogger<DevicesViewModel> logger)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _devices.DevicesChanged += OnDevicesChanged;
        _devices.DeviceUpdated += OnDeviceUpdated;
        ApplySnapshot(_devices.Devices);
    }

    /// <summary>Gets the tracked devices.</summary>
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];

    /// <summary>Gets or sets the selected device.</summary>
    public DeviceRowViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    /// <summary>Gets whether a device is selected.</summary>
    public bool HasSelection => SelectedDevice is not null;

    /// <summary>Gets whether any device is tracked.</summary>
    public bool HasDevices => Devices.Count > 0;

    /// <summary>Gets whether the device list is empty.</summary>
    public bool IsEmpty => Devices.Count == 0;

    /// <summary>Gets the device count label.</summary>
    public string CountText => Devices.Count == 1 ? "1 device" : $"{Devices.Count} devices";

    /// <summary>Gets a summary of the adb connection.</summary>
    public string ConnectionSummary => _devices.IsAdbConnected
        ? "ADB connected"
        : _devices.IsUsingCliFallback
            ? "ADB via CLI fallback"
            : "ADB disconnected";

    /// <summary>Gets whether adb reported a connection problem.</summary>
    public bool HasConnectionProblem => _hasConnectionProblem;

    /// <summary>Gets the connection problem description.</summary>
    public string ConnectionProblemText => _connectionProblemText;

    /// <summary>Gets the empty-state heading.</summary>
    public string EmptyStateTitle => HasConnectionProblem ? "ADB is unavailable" : "No devices connected";

    /// <summary>Gets the empty-state guidance.</summary>
    public string EmptyStateDescription => HasConnectionProblem
        ? _connectionProblemText
        : "Connect a device over USB with USB debugging enabled, or start an emulator. The list updates automatically.";

    /// <summary>Gets whether a command is in progress.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
            }
        }
    }

    /// <summary>Gets whether commands are available.</summary>
    public bool IsNotBusy => !IsBusy;

    /// <summary>Gets the last command result message.</summary>
    public string ActionMessage => _actionMessage;

    /// <summary>Gets whether an action message is available.</summary>
    public bool HasActionMessage => _actionMessage.Length > 0;

    /// <summary>Gets the last command error.</summary>
    public string ErrorMessage => _errorMessage;

    /// <summary>Gets whether a command failed.</summary>
    public bool HasError => _errorMessage.Length > 0;

    /// <summary>Refreshes the page from the current snapshot and re-queries adb.</summary>
    public void Refresh()
    {
        ApplySnapshot(_devices.Devices);
        _ = RefreshInBackgroundAsync();
    }

    /// <summary>Refreshes the device list and reports the result.</summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;
        ClearMessages();
        try
        {
            await _devices.RefreshAsync();
            ApplySnapshot(_devices.Devices);
            SetActionMessage("Device list refreshed.");
        }
        catch (Exception exception)
        {
            ReportFailure("Refreshing the device list", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Refreshes metadata for the selected device.</summary>
    public async Task RefreshMetadataAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var metadata = await _devices.RefreshMetadataAsync(device.Serial);
            device.SetMetadataNote(
                metadata.Warnings.Count == 0
                    ? "Metadata refreshed."
                    : $"Some fields could not be read: {string.Join("; ", metadata.Warnings)}");
            SetActionMessage($"Metadata refreshed for {device.DisplayName}.");
        }
        catch (Exception exception)
        {
            ReportFailure($"Refreshing metadata for {device.DisplayName}", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves notes for the selected device.</summary>
    public async Task SaveNotesAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var notes = string.IsNullOrWhiteSpace(device.Notes) ? null : device.Notes.Trim();
            await _devices.SetNotesAsync(device.Serial, notes);
            device.Notes = notes ?? string.Empty;
            device.MarkNotesSaved();
            SetActionMessage($"Notes saved for {device.DisplayName}.");
        }
        catch (Exception exception)
        {
            ReportFailure($"Saving notes for {device.DisplayName}", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Saves tags for the selected device.</summary>
    public async Task SaveTagsAsync()
    {
        if (SelectedDevice is not { } device)
        {
            return;
        }

        IsBusy = true;
        ClearMessages();
        try
        {
            var tags = device.TagsText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            await _devices.SetTagsAsync(device.Serial, tags);
            device.MarkTagsSaved();
            SetActionMessage($"Tags saved for {device.DisplayName}.");
        }
        catch (Exception exception)
        {
            ReportFailure($"Saving tags for {device.DisplayName}", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Restarts the adb server and reconnects.</summary>
    public async Task RestartAdbAsync()
    {
        IsBusy = true;
        ClearMessages();
        try
        {
            var restarted = await _devices.RestartAdbAsync();
            ApplySnapshot(_devices.Devices);
            SetActionMessage(restarted
                ? "ADB restarted."
                : "ADB could not be restarted. Check the adb path in Settings.");
        }
        catch (Exception exception)
        {
            ReportFailure("Restarting adb", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Sets a success message shown by the page.</summary>
    public void SetActionMessage(string message)
    {
        _actionMessage = message;
        OnPropertyChanged(nameof(ActionMessage));
        OnPropertyChanged(nameof(HasActionMessage));
    }

    /// <summary>Sets an error message shown by the page.</summary>
    public void SetErrorMessage(string message)
    {
        _errorMessage = message;
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>Builds a copyable summary for the selected device.</summary>
    public string BuildSelectedDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine("DroidDeck device summary");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Diagnostics ID: {_diagnostics.DiagnosticsId}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Session ID: {_diagnostics.SessionId}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}");
        builder.AppendLine();
        if (SelectedDevice is { } device)
        {
            builder.AppendLine(device.BuildDetails());
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("No device selected.");
            builder.AppendLine();
        }

        builder.AppendLine("ADB");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Socket: {(_devices.IsAdbConnected ? "connected" : "disconnected")}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"CLI fallback: {(_devices.IsUsingCliFallback ? "active" : "inactive")}");
        if (!string.IsNullOrWhiteSpace(_devices.LastError))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Last error: {_devices.LastError}");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Clears any previously displayed messages.</summary>
    public void ClearMessages()
    {
        if (_actionMessage.Length > 0)
        {
            _actionMessage = string.Empty;
            OnPropertyChanged(nameof(ActionMessage));
            OnPropertyChanged(nameof(HasActionMessage));
        }

        if (_errorMessage.Length > 0)
        {
            _errorMessage = string.Empty;
            OnPropertyChanged(nameof(ErrorMessage));
            OnPropertyChanged(nameof(HasError));
        }
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
        _devices.DeviceUpdated -= OnDeviceUpdated;
        GC.SuppressFinalize(this);
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await _devices.RefreshAsync();
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(exception, "The background device refresh failed.");
            }
        }
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e)
    {
        var snapshot = e.Devices;
        Enqueue(() => ApplySnapshot(snapshot));
    }

    private void OnDeviceUpdated(object? sender, DeviceInfo device)
    {
        Enqueue(() =>
        {
            var row = Devices.FirstOrDefault(existing => existing.Serial == device.Serial);
            row?.Update(device);
        });
    }

    private void ApplySnapshot(IReadOnlyList<DeviceInfo> snapshot)
    {
        for (var index = 0; index < snapshot.Count; index++)
        {
            var device = snapshot[index];
            if (index < Devices.Count && Devices[index].Serial == device.Serial)
            {
                Devices[index].Update(device);
                continue;
            }

            var existing = Devices.FirstOrDefault(row => row.Serial == device.Serial);
            if (existing is null)
            {
                Devices.Insert(index, new DeviceRowViewModel(device));
            }
            else
            {
                Devices.Remove(existing);
                Devices.Insert(index, existing);
                existing.Update(device);
            }
        }

        while (Devices.Count > snapshot.Count)
        {
            Devices.RemoveAt(Devices.Count - 1);
        }

        if (SelectedDevice is not null && !Devices.Contains(SelectedDevice))
        {
            SelectedDevice = null;
        }

        var hadDevices = _hadDevices;
        _hadDevices = Devices.Count > 0;
        if (!hadDevices && _hadDevices && SelectedDevice is null)
        {
            SelectedDevice = Devices[0];
        }

        NotifyListState();
    }

    private void NotifyListState()
    {
        var lastError = _devices.LastError;
        _hasConnectionProblem = !_devices.IsAdbConnected &&
                                !_devices.IsUsingCliFallback &&
                                !string.IsNullOrWhiteSpace(lastError);
        _connectionProblemText = _hasConnectionProblem
            ? $"{lastError} Check the adb path in Settings and retry. Diagnostics ID {_diagnostics.DiagnosticsId}."
            : string.Empty;

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(HasConnectionProblem));
        OnPropertyChanged(nameof(ConnectionProblemText));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
    }

    private void ReportFailure(string action, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(exception, "{Action} failed.", action);
        }

        SetErrorMessage(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{action} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId})."));
    }

    private void Enqueue(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }
}
