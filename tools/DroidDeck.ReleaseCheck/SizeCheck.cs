using System.Globalization;

namespace DroidDeck.ReleaseCheck;

internal sealed record SizeMeasurement(long ExtractedBytes, long? ZipBytes);

internal static class SizeCheck
{
    public static int Run(Options options)
    {
        var directory = Path.GetFullPath(options.Required("directory"));
        if (!Directory.Exists(directory))
        {
            throw new ArgumentException($"The publish directory '{directory}' does not exist.");
        }

        var maxExtractedBytes = (long)(
            options.OptionalDouble("max-extracted-mb", ReleaseBudgets.MaxExtractedMegabytes) *
            1024 * 1024);
        var maxZipBytes = (long)(
            options.OptionalDouble("max-zip-mb", ReleaseBudgets.MaxZipMegabytes) *
            1024 * 1024);
        if (maxExtractedBytes <= 0 || maxZipBytes <= 0)
        {
            throw new ArgumentException("Size budgets must be positive.");
        }

        long? zipBytes = null;
        if (options.Optional("zip") is { } zipPath)
        {
            var fullZipPath = Path.GetFullPath(zipPath);
            if (!File.Exists(fullZipPath))
            {
                throw new ArgumentException($"The zip '{fullZipPath}' does not exist.");
            }

            zipBytes = new FileInfo(fullZipPath).Length;
        }

        var measurement = new SizeMeasurement(DirectorySizer.Measure(directory), zipBytes);
        var result = Evaluate(measurement, maxExtractedBytes, maxZipBytes);

        Console.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Extracted: {ByteSize.Format(measurement.ExtractedBytes)}; " +
                $"budget {ByteSize.Format(maxExtractedBytes)}."));
        if (measurement.ZipBytes is { } zip)
        {
            Console.Out.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Zip: {ByteSize.Format(zip)}; budget {ByteSize.Format(maxZipBytes)}."));
        }

        Reports.WriteSummary(result, "Size budget");
        Reports.Write(
            options.Optional("json"),
            new
            {
                command = "size",
                timestampUtc = DateTimeOffset.UtcNow,
                label = options.Optional("label"),
                passed = result.Passed,
                environment = ReportEnvironment.Current,
                directory,
                extractedBytes = measurement.ExtractedBytes,
                zipBytes = measurement.ZipBytes,
                maxExtractedBytes,
                maxZipBytes,
                failures = result.Failures,
            });
        return result.Passed ? 0 : 1;
    }

    public static CheckResult Evaluate(
        SizeMeasurement measurement,
        long maxExtractedBytes,
        long maxZipBytes)
    {
        var failures = new List<string>();
        if (measurement.ExtractedBytes > maxExtractedBytes)
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The extracted size is {ByteSize.Format(measurement.ExtractedBytes)}, " +
                    $"over the {ByteSize.Format(maxExtractedBytes)} budget."));
        }

        if (measurement.ZipBytes is { } zip && zip > maxZipBytes)
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The zip size is {ByteSize.Format(zip)}, " +
                    $"over the {ByteSize.Format(maxZipBytes)} budget."));
        }

        return new CheckResult(failures);
    }
}

internal static class DirectorySizer
{
    public static long Measure(string directory)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
    }
}
