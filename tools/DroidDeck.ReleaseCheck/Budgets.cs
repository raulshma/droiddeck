using System.Globalization;

namespace DroidDeck.ReleaseCheck;

internal static class ReleaseBudgets
{
    public const double MaxExtractedMegabytes = 250;
    public const double MaxZipMegabytes = 120;
    public const double StartupMedianMilliseconds = 2_000;
    public const double StartupP95Milliseconds = 3_000;
    public const double MaxMemoryGrowthPercent = 5;
    public const int MaxHandleGrowth = 32;
    public const double ReleaseSoakHours = 8;
}

internal sealed record CheckResult(IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

internal static class ByteSize
{
    public static string Format(long bytes) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{bytes / (1024d * 1024d):F1} MiB ({bytes.ToString(CultureInfo.InvariantCulture)} bytes)");
}

internal static class Statistics
{
    public static double Median(IReadOnlyList<double> values) => Percentile(values, 50);

    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        if (percentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var sorted = values.Order().ToArray();
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var position = (sorted.Length - 1) * (percentile / 100);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = position - lower;
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * weight);
    }
}

internal static class ReportEnvironment
{
    public static object Current => new
    {
        machineName = Environment.MachineName,
        osVersion = Environment.OSVersion.VersionString,
        processorCount = Environment.ProcessorCount,
        dotnetVersion = Environment.Version.ToString(),
        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    };
}

internal static class Reports
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void Write(string? path, object report)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            System.Text.Json.JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine);
        Console.Out.WriteLine($"Report: {fullPath}");
    }

    public static void WriteSummary(CheckResult result, string label)
    {
        Console.Out.WriteLine(result.Passed ? $"{label}: PASS" : $"{label}: FAIL");
        foreach (var failure in result.Failures)
        {
            Console.Out.WriteLine("  - " + failure);
        }
    }
}
