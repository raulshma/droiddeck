using System.Collections.ObjectModel;
using DroidDeck.Core.Devices;

namespace DroidDeck.Adb;

/// <summary>Parses the <c>devices -l</c> and <c>track-devices-l</c> payloads.</summary>
public static class AdbDeviceParser
{
    private static readonly Dictionary<string, DeviceState> StateNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["device"] = DeviceState.Device,
        ["offline"] = DeviceState.Offline,
        ["unauthorized"] = DeviceState.Unauthorized,
        ["authorizing"] = DeviceState.Authorizing,
        ["connecting"] = DeviceState.Connecting,
        ["recovery"] = DeviceState.Recovery,
        ["sideload"] = DeviceState.Sideload,
        ["bootloader"] = DeviceState.Bootloader,
        ["detached"] = DeviceState.Disconnected,
        ["disconnected"] = DeviceState.Disconnected,
    };

    private static readonly string[] PreamblePrefixes =
    [
        "List of devices attached",
        "*",
        "adb server",
        "cannot connect",
        "error:",
    ];

    /// <summary>Parses every device line in a payload, sorted by serial.</summary>
    public static IReadOnlyList<DeviceInfo> Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ReadOnlyCollection<DeviceInfo>.Empty;
        }

        var devices = new List<DeviceInfo>();
        foreach (var line in payload.Split('\n'))
        {
            var device = ParseLine(line);
            if (device is not null)
            {
                devices.Add(device);
            }
        }

        devices.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Serial, right.Serial));
        return devices;
    }

    /// <summary>Parses one device line, returning <see langword="null"/> for preambles and blanks.</summary>
    public static DeviceInfo? ParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var text = line.Trim();
        foreach (var prefix in PreamblePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var tokens = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return null;
        }

        var serial = tokens[0];
        if (!IsPlausibleSerial(serial))
        {
            return null;
        }

        var state = DeviceState.Unknown;
        var stateIndex = -1;
        for (var index = 1; index < tokens.Length; index++)
        {
            if (StateNames.TryGetValue(tokens[index], out state))
            {
                stateIndex = index;
                break;
            }

            if (tokens[index].Contains(':', StringComparison.Ordinal))
            {
                break;
            }
        }

        if (stateIndex < 0 && !text.Contains("no permissions", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? product = null;
        string? model = null;
        string? deviceName = null;
        string? transportId = null;

        var attributesStart = stateIndex >= 0 ? stateIndex + 1 : 2;
        for (var index = attributesStart; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var separator = token.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = token[..separator];
            var value = token[(separator + 1)..];
            switch (key)
            {
                case "product":
                    product = NormalizeValue(value);
                    break;
                case "model":
                    model = NormalizeValue(value);
                    break;
                case "device":
                    deviceName = NormalizeValue(value);
                    break;
                case "transport_id":
                    transportId = value;
                    break;
                default:
                    break;
            }
        }

        return new DeviceInfo
        {
            Serial = serial,
            State = state,
            Product = product,
            Model = model,
            Codename = deviceName,
            TransportId = transportId,
            IsEmulator = DetectEmulator(serial, product, deviceName),
        };
    }

    private static bool IsPlausibleSerial(string serial)
    {
        if (serial.Length == 0)
        {
            return false;
        }

        foreach (var character in serial)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string? NormalizeValue(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        return value.Replace('_', ' ');
    }

    private static bool DetectEmulator(string serial, string? product, string? deviceName)
    {
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (product is not null &&
            (product.StartsWith("sdk_gphone", StringComparison.OrdinalIgnoreCase) ||
             product.StartsWith("sdk_google", StringComparison.OrdinalIgnoreCase) ||
             product.Equals("sdk", StringComparison.OrdinalIgnoreCase) ||
             product.StartsWith("generic", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return deviceName is not null &&
            (deviceName.StartsWith("generic", StringComparison.OrdinalIgnoreCase) ||
             deviceName.StartsWith("goldfish", StringComparison.OrdinalIgnoreCase) ||
             deviceName.StartsWith("ranchu", StringComparison.OrdinalIgnoreCase));
    }
}
