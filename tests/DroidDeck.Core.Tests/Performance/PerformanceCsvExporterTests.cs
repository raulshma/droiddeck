using System.Globalization;
using DroidDeck.Core.Performance;

namespace DroidDeck.Core.Tests.Performance;

public sealed class PerformanceCsvExporterTests
{
    [Fact]
    public void Export_WritesAHeaderAndEverySeriesWithTimestamps()
    {
        var timestamp = new DateTimeOffset(2026, 10, 29, 9, 12, 44, TimeSpan.Zero);
        var samples = new[]
        {
            new PerformanceSample
            {
                Timestamp = timestamp,
                CpuPercent = 12.5,
                MemoryMegabytes = 24,
                Fps = 59.5,
                JankPercent = 1.5,
                BatteryPercent = 85,
                BatteryTemperatureCelsius = 32.5,
            },
        };

        var csv = PerformanceCsvExporter.Export(samples);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "timestamp,cpu_percent,memory_mb,fps,jank_percent,battery_percent,battery_temperature_c",
            lines[0]);
        Assert.Equal(
            "2026-10-29T09:12:44.0000000+00:00,12.5,24,59.5,1.5,85,32.5",
            lines[1]);
    }

    [Fact]
    public void Export_LeavesMissingValuesBlank()
    {
        var samples = new[]
        {
            new PerformanceSample
            {
                Timestamp = new DateTimeOffset(2026, 10, 29, 9, 12, 44, TimeSpan.Zero),
                CpuPercent = 10,
            },
        };

        var csv = PerformanceCsvExporter.Export(samples);
        var row = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal("2026-10-29T09:12:44.0000000+00:00,10,,,,,", row);
    }

    [Fact]
    public void Export_UsesInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var samples = new[]
            {
                new PerformanceSample
                {
                    Timestamp = new DateTimeOffset(2026, 10, 29, 9, 12, 44, TimeSpan.Zero),
                    CpuPercent = 12.5,
                },
            };

            var csv = PerformanceCsvExporter.Export(samples);

            Assert.Contains("12.5", csv, StringComparison.Ordinal);
            Assert.DoesNotContain("12,5", csv, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Export_ReturnsOnlyTheHeaderWithoutSamples()
    {
        var csv = PerformanceCsvExporter.Export([]);

        Assert.Equal(
            "timestamp,cpu_percent,memory_mb,fps,jank_percent,battery_percent,battery_temperature_c" +
            Environment.NewLine,
            csv);
    }
}
