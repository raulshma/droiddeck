using System.Globalization;
using System.Net;
using System.Text;
using DroidDeck.Adb.Commands;
using DroidDeck.Adb.Parsing;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Devices;

/// <summary>
/// Fetches device metadata with one sectioned <c>adb shell</c> invocation.
/// </summary>
public sealed class AdbDeviceMetadataSource : IDeviceMetadataSource
{
    /// <summary>The script executed in a single shell invocation.</summary>
    public const string BatchScript =
        "echo ---PROP---; getprop; " +
        "echo ---WM---; wm size; wm density; " +
        "echo ---BAT---; dumpsys battery; " +
        "echo ---NET---; ip -f inet addr show wlan0; " +
        "echo ---DF---; df -k /data";

    private const string PropMarker = "---PROP---";
    private const string WmMarker = "---WM---";
    private const string BatteryMarker = "---BAT---";
    private const string NetworkMarker = "---NET---";
    private const string StorageMarker = "---DF---";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly AdbCommandExecutor _adb;
    private volatile string _adbExecutable;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdbDeviceMetadataSource> _logger;

    /// <summary>Creates a metadata source.</summary>
    public AdbDeviceMetadataSource(
        IProcessRunner processRunner,
        string adbExecutable = "adb",
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null,
        ILogger<AdbDeviceMetadataSource>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(adbExecutable);
        _adbExecutable = adbExecutable;
        _timeout = timeout ?? DefaultTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AdbDeviceMetadataSource>.Instance;
        _adb = new AdbCommandExecutor(processRunner, () => _adbExecutable, _timeProvider, _logger);
    }

    /// <summary>Changes the adb executable used for metadata requests.</summary>
    public void UpdateAdbExecutable(string adbExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adbExecutable);
        _adbExecutable = adbExecutable;
    }

    /// <inheritdoc />
    public async Task<DeviceMetadata> FetchAsync(string serial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);

        var result = await _adb
            .RunForDeviceAsync(
                serial,
                ["shell", BatchScript],
                _timeout,
                new AdbCommandOptions { MaxOutputCharacters = 1024 * 1024 },
                cancellationToken)
            .ConfigureAwait(false);
        var raw = result.Raw;
        var fetchedAt = _timeProvider.GetUtcNow();

        if (DescribeFailure(result) is { } failure)
        {
            return Failure(fetchedAt, failure);
        }

        if (string.IsNullOrWhiteSpace(raw.StandardOutput))
        {
            var detail = AdbCommandFailures.FirstLine(raw.StandardError);
            var reason = detail is null
                ? "Metadata collection returned no output."
                : $"Metadata collection failed: {detail}";
            if (!raw.Succeeded)
            {
                reason += $" (exit code {raw.ExitCode})";
            }

            return Failure(fetchedAt, reason);
        }

        var metadata = ParseBatch(raw.StandardOutput, fetchedAt);
        if (!raw.Succeeded)
        {
            var detail = AdbCommandFailures.FirstLine(raw.StandardError) ?? $"exit code {raw.ExitCode}";

            metadata = metadata with
            {
                Warnings = [.. metadata.Warnings, $"adb reported an error while reading metadata: {detail}"],
            };

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Metadata for {Serial} exited with code {ExitCode}: {Detail}", serial, raw.ExitCode, detail);
            }
        }

        return metadata;
    }

    /// <summary>Parses the sectioned shell output.</summary>
    public static DeviceMetadata ParseBatch(string output, DateTimeOffset fetchedAt)
    {
        var warnings = new List<string>();
        var sections = SplitSections(output);

        Dictionary<string, string>? properties = null;
        if (sections.TryGetValue(PropMarker, out var propText))
        {
            properties = ParseProperties(propText);
        }
        else
        {
            warnings.Add("Device properties were not returned.");
        }

        var manufacturer = GetProperty(properties, "ro.product.manufacturer");
        var model = GetProperty(properties, "ro.product.model");
        var product = GetProperty(properties, "ro.product.name");
        var androidVersion = GetProperty(properties, "ro.build.version.release");
        var apiLevel = ParseInt(GetProperty(properties, "ro.build.version.sdk"));
        var abi = GetProperty(properties, "ro.product.cpu.abi");
        var abiList = ParseList(GetProperty(properties, "ro.product.cpu.abilist"));
        var buildType = GetProperty(properties, "ro.build.type");
        var isDebuggable = ParseFlag(GetProperty(properties, "ro.debuggable"));
        var characteristics = GetProperty(properties, "ro.build.characteristics");
        var qemu = GetProperty(properties, "ro.kernel.qemu") ?? GetProperty(properties, "ro.boot.qemu");
        var avdName = GetProperty(properties, "ro.boot.qemu.avd_name") ??
            GetProperty(properties, "ro.kernel.qemu.avd_name");
        var isEmulator = ParseFlag(qemu) == true || !string.IsNullOrWhiteSpace(avdName);
        if (abiList.Length == 0 && abi is not null)
        {
            abiList = [abi];
        }

        AddMissing(warnings, manufacturer, "The manufacturer was not reported.");
        AddMissing(warnings, model, "The model was not reported.");
        AddMissing(warnings, androidVersion, "The Android version was not reported.");
        if (apiLevel is null)
        {
            warnings.Add("The API level was not reported.");
        }

        AddMissing(warnings, abi, "The ABI was not reported.");

        string? screenSize = null;
        int? density = null;
        if (sections.TryGetValue(WmMarker, out var wmText))
        {
            screenSize = ParseWmValue(wmText, "Override size:", "Physical size:");
            density = ParseInt(ParseWmValue(wmText, "Override density:", "Physical density:"));
            if (screenSize is null)
            {
                warnings.Add("The screen size was not reported.");
            }

            if (density is null)
            {
                warnings.Add("The screen density was not reported.");
            }
        }
        else
        {
            warnings.Add("Screen information was not returned.");
        }

        int? batteryLevel = null;
        string? batteryStatus = null;
        if (sections.TryGetValue(BatteryMarker, out var batteryText))
        {
            var battery = ParseKeyValues(batteryText);
            batteryLevel = ParseInt(battery.GetValueOrDefault("level"));
            if (batteryLevel is < 0 or > 100)
            {
                batteryLevel = null;
            }

            batteryStatus = MapBatteryStatus(battery.GetValueOrDefault("status"));
            if (batteryLevel is null)
            {
                warnings.Add("The battery level was not reported.");
            }
        }
        else
        {
            warnings.Add("Battery information was not returned.");
        }

        string? ipAddress = null;
        if (sections.TryGetValue(NetworkMarker, out var networkText))
        {
            ipAddress = ParseIpAddress(networkText);
        }
        else
        {
            warnings.Add("Network information was not returned.");
        }

        long? storageTotal = null;
        long? storageFree = null;
        if (sections.TryGetValue(StorageMarker, out var storageText))
        {
            // Metadata only trusts an exact /data row; it does not fall back to another mount.
            var data = DfOutput.FindMountPoint(DfOutput.Parse(storageText), DfOutput.DataMountPoint);
            storageTotal = data?.TotalBytes;
            storageFree = data?.AvailableBytes;
            if (storageTotal is null || storageFree is null)
            {
                warnings.Add("Storage information could not be read.");
            }
        }
        else
        {
            warnings.Add("Storage information was not returned.");
        }

        return new DeviceMetadata
        {
            Manufacturer = manufacturer,
            Model = model,
            Product = product,
            AndroidVersion = androidVersion,
            ApiLevel = apiLevel,
            Abi = abi,
            AbiList = abiList,
            IsEmulator = isEmulator,
            AvdName = avdName,
            ScreenSize = screenSize,
            Density = density,
            BatteryLevel = batteryLevel,
            BatteryStatus = batteryStatus,
            IpAddress = ipAddress,
            BuildType = buildType,
            IsDebuggable = isDebuggable,
            Characteristics = characteristics,
            StorageTotalBytes = storageTotal,
            StorageFreeBytes = storageFree,
            Warnings = warnings,
            FetchedAt = fetchedAt,
        };
    }

    private static DeviceMetadata Failure(DateTimeOffset fetchedAt, string reason) => new()
    {
        Warnings = [reason],
        FetchedAt = fetchedAt,
    };

    /// <summary>Maps the executor's failure category to the metadata wording users see.</summary>
    // Only unusable-output failures are hard failures. Classified device
    // signals such as unauthorized or offline can arrive with partial output,
    // which the caller degrades to a warning instead of discarding it.
    private static string? DescribeFailure(AdbCommandResult result) => result.FailureKind switch
    {
        AdbFailureKind.StartError => result.FailureMessage ?? $"adb could not start: {result.StartError}",
        AdbFailureKind.TimedOut => "Metadata collection timed out.",
        AdbFailureKind.Canceled => "Metadata collection was canceled.",
        _ => null,
    };

    private static Dictionary<string, string> SplitSections(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var builder = new StringBuilder();

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed is PropMarker or WmMarker or BatteryMarker or NetworkMarker or StorageMarker)
            {
                if (current is not null)
                {
                    sections[current] = builder.ToString();
                }

                current = trimmed;
                builder.Clear();
                continue;
            }

            if (current is not null)
            {
                builder.Append(line.TrimEnd('\r')).Append('\n');
            }
        }

        if (current is not null)
        {
            sections[current] = builder.ToString();
        }

        return sections;
    }

    private static Dictionary<string, string> ParseProperties(string text)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 4 || trimmed[0] != '[')
            {
                continue;
            }

            var separator = trimmed.IndexOf("]: [", StringComparison.Ordinal);
            if (separator <= 1 || !trimmed.EndsWith(']'))
            {
                continue;
            }

            var key = trimmed[1..separator];
            var value = trimmed[(separator + 4)..^1];
            properties[key] = value;
        }

        return properties;
    }

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static string? ParseWmValue(string text, string overridePrefix, string physicalPrefix)
    {
        string? fallback = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(overridePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return NullIfWhiteSpace(trimmed[overridePrefix.Length..]);
            }

            if (trimmed.StartsWith(physicalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                fallback = NullIfWhiteSpace(trimmed[physicalPrefix.Length..]);
            }
        }

        return fallback;
    }

    private static string? ParseIpAddress(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf("inet ", StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            var value = line[(index + 5)..].Trim();
            var separator = value.IndexOfAny([' ', '/']);
            if (separator >= 0)
            {
                value = value[..separator];
            }

            if (IPAddress.TryParse(value, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(address))
            {
                return value;
            }
        }

        return null;
    }

    private static string? MapBatteryStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return status.Trim() switch
        {
            "1" => "Unknown",
            "2" => "Charging",
            "3" => "Discharging",
            "4" => "Not charging",
            "5" => "Full",
            _ => status.Trim(),
        };
    }

    private static string? GetProperty(Dictionary<string, string>? properties, string key)
    {
        if (properties is null)
        {
            return null;
        }

        return properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
    }

    private static string[] ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static bool? ParseFlag(string? value) => value switch
    {
        "1" => true,
        "0" => false,
        _ => null,
    };

    private static string? NullIfWhiteSpace(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void AddMissing(List<string> warnings, string? value, string warning)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            warnings.Add(warning);
        }
    }
}
