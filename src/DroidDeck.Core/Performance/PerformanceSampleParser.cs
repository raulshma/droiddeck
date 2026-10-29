using System.Globalization;
using System.Text.RegularExpressions;

namespace DroidDeck.Core.Performance;

/// <summary>
/// Parses the dumpsys sections used by live performance sampling. Every parser is
/// tolerant of missing values so one unsupported command does not discard a sample.
/// </summary>
public static partial class PerformanceSampleParser
{
    /// <summary>
    /// Reads the application CPU percentage from <c>dumpsys cpuinfo</c>, falling
    /// back to the device total when the application is not listed.
    /// </summary>
    public static double? ParseCpuPercent(string output, string? package = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (!string.IsNullOrWhiteSpace(package))
        {
            foreach (var line in output.Split('\n'))
            {
                var match = CpuProcessLine().Match(line);
                if (match.Success &&
                    match.Groups["process"].Value.StartsWith(package, StringComparison.OrdinalIgnoreCase))
                {
                    return ParsePercent(match.Groups["value"].Value);
                }
            }
        }

        var total = output
            .Split('\n')
            .FirstOrDefault(line => line.Contains("TOTAL:", StringComparison.OrdinalIgnoreCase));
        if (total is null)
        {
            return null;
        }

        var percentages = PercentValue().Matches(total);
        if (percentages.Count == 0)
        {
            return null;
        }

        var sum = 0d;
        foreach (Match percentage in percentages)
        {
            sum += ParsePercent(percentage.Groups["value"].Value) ?? 0;
        }

        return Math.Round(sum, 1);
    }

    /// <summary>Reads the total PSS memory in megabytes from <c>dumpsys meminfo</c>.</summary>
    public static double? ParseMemoryMegabytes(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var totalPss = TotalPss().Match(output);
        if (totalPss.Success && long.TryParse(
            totalPss.Groups["value"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var bytes))
        {
            return Math.Round(bytes / 1024d, 1);
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("TOTAL ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = TotalRow().Match(trimmed);
            if (match.Success && long.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var kilobytes))
            {
                return Math.Round(kilobytes / 1024d, 1);
            }
        }

        return null;
    }

    /// <summary>Reads cumulative frame counters from <c>dumpsys gfxinfo</c>.</summary>
    public static PerformanceFrameStats? ParseFrameStats(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var total = TotalFrames().Match(output);
        var janky = JankyFrames().Match(output);
        if (!total.Success || !janky.Success ||
            !int.TryParse(total.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalFrames) ||
            !int.TryParse(janky.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var jankyFrames))
        {
            return null;
        }

        return new PerformanceFrameStats
        {
            TotalFrames = totalFrames,
            JankyFrames = jankyFrames,
            JankPercent = janky.Groups["percent"].Success
                ? ParsePercent(janky.Groups["percent"].Value)
                : totalFrames > 0
                    ? Math.Round(jankyFrames * 100d / totalFrames, 1)
                    : null,
        };
    }

    /// <summary>Reads the battery level and temperature from <c>dumpsys battery</c>.</summary>
    public static PerformanceBatteryState ParseBattery(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        int? level = null;
        double? temperature = null;
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (string.Equals(name, "level", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel))
            {
                level = parsedLevel;
            }
            else if (string.Equals(name, "temperature", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var tenths))
            {
                temperature = Math.Round(tenths / 10d, 1);
            }
        }

        return new PerformanceBatteryState { Level = level, TemperatureCelsius = temperature };
    }

    /// <summary>
    /// Computes FPS and jank from two cumulative snapshots. The first sample has no
    /// frame rate because there is no elapsed window yet.
    /// </summary>
    public static PerformanceFrameRates ComputeFrameRates(
        PerformanceFrameStats? previous,
        PerformanceFrameStats current,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (previous is null || elapsed <= TimeSpan.Zero)
        {
            return new PerformanceFrameRates { Fps = null, JankPercent = current.JankPercent };
        }

        var frames = current.TotalFrames - previous.TotalFrames;
        var janky = current.JankyFrames - previous.JankyFrames;
        if (frames <= 0)
        {
            return new PerformanceFrameRates { Fps = 0, JankPercent = current.JankPercent };
        }

        return new PerformanceFrameRates
        {
            Fps = Math.Round(frames / elapsed.TotalSeconds, 1),
            JankPercent = Math.Round(janky * 100d / frames, 1),
        };
    }

    private static double? ParsePercent(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    [GeneratedRegex(@"^\s*(?<value>\d+(?:\.\d+)?)%\s+\d+/(?<process>[^:]+):", RegexOptions.CultureInvariant)]
    private static partial Regex CpuProcessLine();

    [GeneratedRegex(@"(?<value>\d+(?:\.\d+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentValue();

    [GeneratedRegex(@"TOTAL PSS:\s*(?<value>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TotalPss();

    [GeneratedRegex(@"^TOTAL\s+(?<value>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TotalRow();

    [GeneratedRegex(@"Total frames rendered:\s*(?<value>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TotalFrames();

    [GeneratedRegex(@"Janky frames(?: \(legacy\))?:\s*(?<value>\d+)(?:\s*\((?<percent>\d+(?:\.\d+)?)%\))?", RegexOptions.CultureInvariant)]
    private static partial Regex JankyFrames();
}
