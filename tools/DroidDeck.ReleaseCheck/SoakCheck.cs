using System.Diagnostics;
using System.Globalization;

namespace DroidDeck.ReleaseCheck;

internal sealed record SoakSample(
    DateTimeOffset TimestampUtc,
    double ElapsedSeconds,
    long WorkingSetBytes,
    int HandleCount);

internal sealed record SoakSummary(
    int SampleCount,
    int WindowSampleCount,
    double BaselineWorkingSetBytes,
    double FinalWorkingSetBytes,
    double MemoryGrowthPercent,
    double BaselineHandles,
    double FinalHandles,
    double HandleGrowth,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

internal static class SoakAnalyzer
{
    private const int MinimumWindowSamples = 6;

    public static SoakSummary Analyze(
        IReadOnlyList<SoakSample> samples,
        double warmupSeconds,
        double maxMemoryGrowthPercent,
        int maxHandleGrowth)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegative(warmupSeconds);
        var window = samples
            .Where(sample => sample.ElapsedSeconds >= warmupSeconds)
            .ToArray();
        var failures = new List<string>();
        if (window.Length < MinimumWindowSamples)
        {
            failures.Add(
                $"Only {window.Length} sample(s) were collected after warmup; " +
                $"at least {MinimumWindowSamples} are needed to judge growth.");
            return new SoakSummary(
                samples.Count,
                window.Length,
                0,
                0,
                0,
                0,
                0,
                0,
                failures);
        }

        var groupSize = Math.Max(3, window.Length / 10);
        var baseline = window.Take(groupSize).ToArray();
        var final = window.TakeLast(groupSize).ToArray();
        var baselineWorkingSet = Statistics.Median(
            baseline.Select(sample => (double)sample.WorkingSetBytes).ToArray());
        var finalWorkingSet = Statistics.Median(
            final.Select(sample => (double)sample.WorkingSetBytes).ToArray());
        var baselineHandles = Statistics.Median(
            baseline.Select(sample => (double)sample.HandleCount).ToArray());
        var finalHandles = Statistics.Median(
            final.Select(sample => (double)sample.HandleCount).ToArray());
        var memoryGrowthPercent = baselineWorkingSet <= 0
            ? 0
            : ((finalWorkingSet - baselineWorkingSet) / baselineWorkingSet) * 100;
        var handleGrowth = finalHandles - baselineHandles;

        if (memoryGrowthPercent >= maxMemoryGrowthPercent)
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The working set grew {memoryGrowthPercent:F1} percent " +
                    $"({ByteSize.Format((long)baselineWorkingSet)} to {ByteSize.Format((long)finalWorkingSet)}), " +
                    $"at or over the {maxMemoryGrowthPercent:F1} percent budget."));
        }

        if (handleGrowth > maxHandleGrowth)
        {
            failures.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The handle count grew by {handleGrowth:F0} " +
                    $"({baselineHandles:F0} to {finalHandles:F0}), over the {maxHandleGrowth} handle budget."));
        }

        return new SoakSummary(
            samples.Count,
            window.Length,
            baselineWorkingSet,
            finalWorkingSet,
            memoryGrowthPercent,
            baselineHandles,
            finalHandles,
            handleGrowth,
            failures);
    }
}

internal static class SoakCheck
{
    internal static bool IsReleaseEligible(TimeSpan duration) =>
        duration >= TimeSpan.FromHours(ReleaseBudgets.ReleaseSoakHours);

    public static async Task<int> RunAsync(Options options, CancellationToken cancellationToken)
    {
        var duration = options.OptionalDuration("duration", TimeSpan.FromHours(8));
        var interval = options.OptionalDuration("interval", TimeSpan.FromSeconds(10));
        var warmup = options.OptionalDuration("warmup", TimeSpan.FromMinutes(5));
        var maxMemoryGrowth = options.OptionalDouble(
            "max-memory-growth-percent",
            ReleaseBudgets.MaxMemoryGrowthPercent);
        var maxHandleGrowth = options.OptionalInt("max-handle-growth", ReleaseBudgets.MaxHandleGrowth);
        if (duration <= TimeSpan.Zero || interval <= TimeSpan.Zero || interval >= duration)
        {
            throw new ArgumentException(
                "--duration must be positive and --interval must be positive and shorter than it.");
        }

        if (warmup < TimeSpan.Zero || warmup >= duration)
        {
            throw new ArgumentException("--warmup must be non-negative and shorter than --duration.");
        }

        if (maxMemoryGrowth <= 0 || maxHandleGrowth < 0)
        {
            throw new ArgumentException("The soak budgets must be positive.");
        }

        var releaseEligible = IsReleaseEligible(duration);
        if (!releaseEligible)
        {
            Console.Out.WriteLine(
                $"This run is shorter than {ReleaseBudgets.ReleaseSoakHours:F0} h; " +
                "it validates the harness but is not release evidence.");
        }

        using var process = ResolveProcess(options);
        Console.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Sampling process {process.Id} ({process.ProcessName}) every " +
                $"{interval.TotalSeconds:F0} s for {duration.TotalHours:F2} h " +
                $"after a {warmup.TotalMinutes:F1} min warmup."));
        var samples = new List<SoakSample>();
        var stopwatch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The process exited after {stopwatch.Elapsed.TotalSeconds:F0} s; " +
                    "the soak cannot continue.");
            }

            samples.Add(
                new SoakSample(
                    DateTimeOffset.UtcNow,
                    stopwatch.Elapsed.TotalSeconds,
                    process.WorkingSet64,
                    process.HandleCount));
            if (stopwatch.Elapsed >= duration)
            {
                break;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
        while (stopwatch.Elapsed < duration);

        var summary = SoakAnalyzer.Analyze(
            samples,
            warmup.TotalSeconds,
            maxMemoryGrowth,
            maxHandleGrowth);
        Console.Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Samples {summary.SampleCount}; after warmup {summary.WindowSampleCount}; " +
                $"memory growth {summary.MemoryGrowthPercent:F1} percent " +
                $"(budget {maxMemoryGrowth:F1}); handle growth {summary.HandleGrowth:F0} " +
                $"(budget {maxHandleGrowth})."));
        Reports.WriteSummary(new CheckResult(summary.Failures), "Soak budget");
        Reports.Write(
            options.Optional("json"),
            new
            {
                command = "soak",
                timestampUtc = DateTimeOffset.UtcNow,
                label = options.Optional("label"),
                passed = summary.Passed,
                releaseEligible,
                environment = ReportEnvironment.Current,
                processId = process.Id,
                processName = process.ProcessName,
                durationSeconds = duration.TotalSeconds,
                intervalSeconds = interval.TotalSeconds,
                warmupSeconds = warmup.TotalSeconds,
                maxMemoryGrowthPercent = maxMemoryGrowth,
                maxHandleGrowth,
                sampleCount = summary.SampleCount,
                windowSampleCount = summary.WindowSampleCount,
                baselineWorkingSetBytes = summary.BaselineWorkingSetBytes,
                finalWorkingSetBytes = summary.FinalWorkingSetBytes,
                memoryGrowthPercent = summary.MemoryGrowthPercent,
                baselineHandles = summary.BaselineHandles,
                finalHandles = summary.FinalHandles,
                handleGrowth = summary.HandleGrowth,
                failures = summary.Failures,
                samples = samples.Select(sample => new
                {
                    timestampUtc = sample.TimestampUtc,
                    elapsedSeconds = sample.ElapsedSeconds,
                    workingSetBytes = sample.WorkingSetBytes,
                    handleCount = sample.HandleCount,
                }),
            });
        return summary.Passed ? 0 : 1;
    }

    private static Process ResolveProcess(Options options)
    {
        var pidValue = options.Optional("pid");
        if (pidValue is not null)
        {
            if (!int.TryParse(pidValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
                pid <= 0)
            {
                throw new ArgumentException($"The process id '{pidValue}' is not valid.");
            }

            return Process.GetProcessById(pid);
        }

        var name = options.Optional("process") ??
            throw new ArgumentException("The option '--pid' or '--process' is required.");
        var matches = Process.GetProcessesByName(name);
        if (matches.Length == 1)
        {
            return matches[0];
        }

        foreach (var match in matches)
        {
            match.Dispose();
        }

        throw new ArgumentException(
            matches.Length == 0
                ? $"No running process named '{name}' was found. Start DroidDeck first."
                : $"More than one process is named '{name}'; pass --pid instead.");
    }
}
