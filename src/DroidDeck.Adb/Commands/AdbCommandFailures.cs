using DroidDeck.Core.Processes;

namespace DroidDeck.Adb.Commands;

/// <summary>Classifies raw adb process results into the shared failure vocabulary.</summary>
internal static class AdbCommandFailures
{
    /// <summary>Returns the failure category and user-facing message for one invocation.</summary>
    internal static AdbFailureKind Classify(ProcessResult result, out string? message)
    {
        if (!string.IsNullOrWhiteSpace(result.StartError))
        {
            message = $"adb could not start: {result.StartError}";
            return AdbFailureKind.StartError;
        }

        if (result.TimedOut)
        {
            message = "adb timed out.";
            return AdbFailureKind.TimedOut;
        }

        if (result.WasCanceled)
        {
            message = "adb was canceled.";
            return AdbFailureKind.Canceled;
        }

        if (FindSignal(result.StandardOutput, out var kind, out message) ||
            FindSignal(result.StandardError, out kind, out message))
        {
            return kind;
        }

        if (result.ExitCode != 0)
        {
            var detail = FirstLine(result.StandardError) ?? FirstLine(result.StandardOutput);
            message = detail is null
                ? $"adb exited with code {result.ExitCode}."
                : $"adb exited with code {result.ExitCode}: {detail}";
            return AdbFailureKind.ExitCode;
        }

        message = null;
        return AdbFailureKind.None;
    }

    /// <summary>Returns the first non-blank line, trimmed.</summary>
    internal static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0)
            {
                return line;
            }
        }

        return null;
    }

    private static bool FindSignal(string? text, out AdbFailureKind kind, out string? message)
    {
        kind = AdbFailureKind.None;
        message = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var normalized = line.StartsWith("adb: ", StringComparison.OrdinalIgnoreCase)
                ? line["adb: ".Length..].TrimStart()
                : line;
            if (TryMatch(normalized, out kind))
            {
                message = line;
                return true;
            }
        }

        return false;
    }

    private static bool TryMatch(string text, out AdbFailureKind kind)
    {
        if (text.StartsWith("no devices/emulators found", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("no devices found", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.NoDevices;
            return true;
        }

        if (text.Contains("device not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no such device", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("device", StringComparison.OrdinalIgnoreCase) &&
             text.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            kind = AdbFailureKind.DeviceNotFound;
            return true;
        }

        if (text.StartsWith("device unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.Unauthorized;
            return true;
        }

        if (text.StartsWith("device offline", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("offline", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device offline", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device still connecting", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("device still authorizing", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.Offline;
            return true;
        }

        if (text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unable to connect", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot resolve", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("unknown host", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no route to host", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.ConnectionFailure;
            return true;
        }

        if (text.Equals("ko", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("error ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("failure", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("failed", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("cannot ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("unable to ", StringComparison.OrdinalIgnoreCase))
        {
            kind = AdbFailureKind.AdbError;
            return true;
        }

        kind = AdbFailureKind.None;
        return false;
    }
}
