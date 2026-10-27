using System.Globalization;
using System.Text;
using DroidDeck.Core.Devices;

namespace DroidDeck.App.ViewModels;

/// <summary>Presents one tracked device in the device list and overview pane.</summary>
public sealed class DeviceRowViewModel : ObservableObject
{
    private static readonly string[] PropertyNames =
    [
        nameof(Serial),
        nameof(DisplayName),
        nameof(ManufacturerLine),
        nameof(ListSubtitle),
        nameof(State),
        nameof(StateLabel),
        nameof(Guidance),
        nameof(AttentionTitle),
        nameof(HasAttention),
        nameof(IsReady),
        nameof(ConnectionLabel),
        nameof(AndroidLabel),
        nameof(AbiLabel),
        nameof(AbiListLabel),
        nameof(BatteryLabel),
        nameof(StorageLabel),
        nameof(IpLabel),
        nameof(ScreenLabel),
        nameof(BuildLabel),
        nameof(DebuggableLabel),
        nameof(CharacteristicsLabel),
        nameof(ProductLabel),
        nameof(TransportLabel),
        nameof(AvdLabel),
        nameof(LastSeenLabel),
        nameof(MetadataLabel),
        nameof(TagsSummary),
        nameof(Notes),
        nameof(TagsText),
        nameof(MetadataNote),
        nameof(HasMetadataNote),
        nameof(IsNotesDirty),
        nameof(IsTagsDirty),
    ];

    private DeviceInfo _device;
    private string _notes;
    private string _notesBaseline;
    private string _tagsText;
    private string _tagsBaseline;
    private string? _metadataNote;

    /// <summary>Creates a row for a device.</summary>
    public DeviceRowViewModel(DeviceInfo device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _notes = device.Notes ?? string.Empty;
        _notesBaseline = _notes;
        _tagsText = FormatTags(device.Tags);
        _tagsBaseline = _tagsText;
    }

    /// <summary>Gets the adb serial.</summary>
    public string Serial => _device.Serial;

    /// <summary>Gets the display name.</summary>
    public string DisplayName => _device.DisplayName;

    /// <summary>Gets the manufacturer and model line.</summary>
    public string ManufacturerLine
    {
        get
        {
            var manufacturer = string.IsNullOrWhiteSpace(_device.Manufacturer)
                ? "Unknown manufacturer"
                : _device.Manufacturer;
            return $"{manufacturer} · {DisplayName}";
        }
    }

    /// <summary>Gets the secondary list line.</summary>
    public string ListSubtitle => $"{Serial} · {ConnectionLabel}";

    /// <summary>Gets the transport state.</summary>
    public DeviceState State => _device.State;

    /// <summary>Gets the friendly state name.</summary>
    public string StateLabel => State switch
    {
        DeviceState.Device => "Ready",
        DeviceState.Unauthorized => "Unauthorized",
        DeviceState.Authorizing => "Authorizing",
        DeviceState.Offline => "Offline",
        DeviceState.Connecting => "Connecting",
        DeviceState.Recovery => "Recovery",
        DeviceState.Sideload => "Sideload",
        DeviceState.Bootloader => "Bootloader",
        DeviceState.Disconnected => "Disconnected",
        _ => "Unknown",
    };

    /// <summary>Gets next-step guidance for the current state.</summary>
    public string Guidance => DeviceStateGuidance.For(State);

    /// <summary>Gets whether the device needs user attention.</summary>
    public bool HasAttention => State != DeviceState.Device;

    /// <summary>Gets whether the device is ready for actions.</summary>
    public bool IsReady => State == DeviceState.Device;

    /// <summary>Gets the attention heading for the current state.</summary>
    public string AttentionTitle => State switch
    {
        DeviceState.Unauthorized => "Authorization required",
        DeviceState.Authorizing => "Waiting for authorization",
        DeviceState.Offline => "Device offline",
        DeviceState.Connecting => "Connecting",
        DeviceState.Recovery => "Recovery mode",
        DeviceState.Sideload => "Sideload mode",
        DeviceState.Bootloader => "Bootloader",
        DeviceState.Disconnected => "Device disconnected",
        DeviceState.Device => "Ready",
        _ => "Device state unknown",
    };

    /// <summary>Gets the connection kind label.</summary>
    public string ConnectionLabel => _device.ConnectionKind switch
    {
        DeviceConnectionKind.Usb => "USB",
        DeviceConnectionKind.Wireless => "Wi-Fi",
        DeviceConnectionKind.Emulator => "Emulator",
        _ => "Unknown connection",
    };

    /// <summary>Gets the Android version and API level.</summary>
    public string AndroidLabel
    {
        get
        {
            var hasVersion = !string.IsNullOrWhiteSpace(_device.AndroidVersion);
            var hasApi = _device.ApiLevel is not null;
            return (hasVersion, hasApi) switch
            {
                (true, true) => $"Android {_device.AndroidVersion} · API {_device.ApiLevel}",
                (true, false) => $"Android {_device.AndroidVersion}",
                (false, true) => $"API {_device.ApiLevel}",
                _ => "Unknown",
            };
        }
    }

    /// <summary>Gets the primary ABI.</summary>
    public string AbiLabel => _device.Abi ?? (_device.AbiList.Count > 0 ? _device.AbiList[0] : "Unknown");

    /// <summary>Gets all supported ABIs.</summary>
    public string AbiListLabel => _device.AbiList.Count == 0 ? "Unknown" : string.Join(", ", _device.AbiList);

    /// <summary>Gets the battery summary.</summary>
    public string BatteryLabel
    {
        get
        {
            if (_device.BatteryLevel is not { } level)
            {
                return "Unknown";
            }

            return string.IsNullOrWhiteSpace(_device.BatteryStatus)
                ? $"{level}%"
                : $"{level}% · {_device.BatteryStatus}";
        }
    }

    /// <summary>Gets the storage summary.</summary>
    public string StorageLabel
    {
        get
        {
            var total = _device.StorageTotalBytes;
            var free = _device.StorageFreeBytes;
            if (total is null)
            {
                return "Unknown";
            }

            return free is null
                ? FormatBytes(total.Value)
                : $"{FormatBytes(free.Value)} free of {FormatBytes(total.Value)}";
        }
    }

    /// <summary>Gets the Wi-Fi IP address.</summary>
    public string IpLabel => _device.IpAddress ?? "Unknown";

    /// <summary>Gets the screen summary.</summary>
    public string ScreenLabel
    {
        get
        {
            var size = string.IsNullOrWhiteSpace(_device.ScreenSize) ? "Unknown" : _device.ScreenSize;
            return _device.Density is { } density ? $"{size} · {density} dpi" : size;
        }
    }

    /// <summary>Gets the build summary.</summary>
    public string BuildLabel => string.IsNullOrWhiteSpace(_device.BuildType) ? "Unknown" : _device.BuildType;

    /// <summary>Gets whether the build is debuggable.</summary>
    public string DebuggableLabel => _device.IsDebuggable switch
    {
        true => "Yes",
        false => "No",
        null => "Unknown",
    };

    /// <summary>Gets the device characteristics.</summary>
    public string CharacteristicsLabel =>
        string.IsNullOrWhiteSpace(_device.Characteristics) ? "Unknown" : _device.Characteristics;

    /// <summary>Gets the product name.</summary>
    public string ProductLabel => _device.Product ?? "Unknown";

    /// <summary>Gets the transport identifier.</summary>
    public string TransportLabel => _device.TransportId ?? "Unknown";

    /// <summary>Gets the emulator AVD name.</summary>
    public string AvdLabel => string.IsNullOrWhiteSpace(_device.AvdName)
        ? _device.IsEmulator ? "Emulator" : "Not an emulator"
        : _device.AvdName;

    /// <summary>Gets when the device was last seen.</summary>
    public string LastSeenLabel => _device.LastSeenAt == DateTimeOffset.MinValue
        ? "Not seen yet"
        : $"Seen {RelativeTime(_device.LastSeenAt)}";

    /// <summary>Gets when metadata was last refreshed.</summary>
    public string MetadataLabel => _device.MetadataUpdatedAt is { } updated
        ? $"Metadata updated {RelativeTime(updated)}"
        : "Metadata not fetched yet";

    /// <summary>Gets a tag summary.</summary>
    public string TagsSummary => _device.Tags.Count == 0 ? "No tags" : string.Join(", ", _device.Tags);

    /// <summary>Gets or sets user notes.</summary>
    public string Notes
    {
        get => _notes;
        set
        {
            if (SetProperty(ref _notes, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsNotesDirty));
            }
        }
    }

    /// <summary>Gets or sets the comma-separated tag editor value.</summary>
    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (SetProperty(ref _tagsText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsTagsDirty));
            }
        }
    }

    /// <summary>Gets whether notes differ from the last saved value.</summary>
    public bool IsNotesDirty => !string.Equals(_notes, _notesBaseline, StringComparison.Ordinal);

    /// <summary>Gets whether tags differ from the last saved value.</summary>
    public bool IsTagsDirty => !string.Equals(_tagsText, _tagsBaseline, StringComparison.Ordinal);

    /// <summary>Gets a note about the last metadata refresh, if any.</summary>
    public string MetadataNote => _metadataNote ?? string.Empty;

    /// <summary>Gets whether a metadata note is present.</summary>
    public bool HasMetadataNote => !string.IsNullOrWhiteSpace(_metadataNote);

    /// <summary>Applies a device snapshot to this row.</summary>
    public void Update(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;

        if (!IsNotesDirty)
        {
            _notesBaseline = device.Notes ?? string.Empty;
            _notes = _notesBaseline;
        }

        if (!IsTagsDirty)
        {
            _tagsBaseline = FormatTags(device.Tags);
            _tagsText = _tagsBaseline;
        }

        foreach (var property in PropertyNames)
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>Records that the current notes were saved.</summary>
    public void MarkNotesSaved()
    {
        _notesBaseline = _notes;
        OnPropertyChanged(nameof(IsNotesDirty));
    }

    /// <summary>Records that the current tags were saved.</summary>
    public void MarkTagsSaved()
    {
        _tagsBaseline = _tagsText;
        OnPropertyChanged(nameof(IsTagsDirty));
    }

    /// <summary>Shows a note about the last metadata refresh.</summary>
    public void SetMetadataNote(string? note)
    {
        _metadataNote = note;
        OnPropertyChanged(nameof(MetadataNote));
        OnPropertyChanged(nameof(HasMetadataNote));
    }

    /// <summary>Builds a plain-text summary for copying into a support request.</summary>
    public string BuildDetails()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Device: {DisplayName}");
        builder.AppendLine($"Serial: {Serial}");
        builder.AppendLine($"State: {StateLabel}");
        builder.AppendLine($"Connection: {ConnectionLabel}");
        builder.AppendLine($"Android: {AndroidLabel}");
        builder.AppendLine($"ABI: {AbiLabel}");
        builder.AppendLine($"Product: {ProductLabel}");
        builder.AppendLine($"AVD: {AvdLabel}");
        builder.AppendLine($"Build: {BuildLabel} (debuggable: {DebuggableLabel})");
        builder.AppendLine($"Metadata: {MetadataLabel}");
        builder.AppendLine($"Last seen: {LastSeenLabel}");
        builder.AppendLine($"Guidance: {Guidance}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatTags(IReadOnlyList<string> tags) => string.Join(", ", tags);

    private static string FormatBytes(long bytes)
    {
        const double Gigabyte = 1024d * 1024 * 1024;
        const double Megabyte = 1024d * 1024;
        if (bytes >= Gigabyte)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / Gigabyte:0.#} GB");
        }

        if (bytes >= Megabyte)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / Megabyte:0.#} MB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
    }

    private static string RelativeTime(DateTimeOffset timestamp)
    {
        var delta = DateTimeOffset.UtcNow - timestamp;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 45)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 2)
        {
            return "1 minute ago";
        }

        if (delta.TotalMinutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalMinutes} minutes ago");
        }

        if (delta.TotalHours < 2)
        {
            return "1 hour ago";
        }

        if (delta.TotalHours < 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalHours} hours ago");
        }

        if (delta.TotalDays < 2)
        {
            return "yesterday";
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)delta.TotalDays} days ago");
    }
}
