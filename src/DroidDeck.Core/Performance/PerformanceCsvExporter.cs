using System.Globalization;
using System.Text;

namespace DroidDeck.Core.Performance;

/// <summary>Exports sampled performance series as CSV for spreadsheets and reports.</summary>
public static class PerformanceCsvExporter
{
    private const string Header =
        "timestamp,cpu_percent,memory_mb,fps,jank_percent,battery_percent,battery_temperature_c";

    /// <summary>Writes a header followed by one row per sample using invariant culture.</summary>
    public static string Export(IReadOnlyList<PerformanceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var builder = new StringBuilder();
        builder.Append(Header).AppendLine();
        foreach (var sample in samples)
        {
            builder
                .Append(sample.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(Format(sample.CpuPercent)).Append(',')
                .Append(Format(sample.MemoryMegabytes)).Append(',')
                .Append(Format(sample.Fps)).Append(',')
                .Append(Format(sample.JankPercent)).Append(',')
                .Append(Format(sample.BatteryPercent)).Append(',')
                .Append(Format(sample.BatteryTemperatureCelsius))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Format(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
