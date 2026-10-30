using System.Diagnostics;
using System.Globalization;

namespace DroidDeck.ReleaseCheck;

internal sealed record StartupSummary(
    int Runs,
    int WarmupRuns,
    double MedianMilliseconds,
    double P95Milliseconds,
    double MaxMilliseconds,
    IReadOnlyList<double> Samples,
    IReadOnlyList<int> TimedOutRuns,
    IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

internal static class StartupAnalyzer
{
    public static StartupSummary Analyze(
        IReadOnlyList<double> samples,
        IReadOnlyList<int> timedOutRuns,
        int runs,
        int warmupRuns,
        double medianBudgetMilliseconds,
        double p95BudgetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(timedOutRuns);
        var failures = new List<string>();
        if (timedOutRuns.Count > 0)
        {
            failures.Add(
                $"{timedOutRuns.Count} run(s) did not show the main window before the timeout: " +
                string.Join(", ", timedOutRuns.Select(run => run.ToString(CultureInfo.InvariantCulture))) + ".");
        }

        var median = 0d;
        var p95 = 0d;
        var max = 0d;
        if (samples.Count == 0)
        {
            failures.Add("No startup samples were collected.");
        }
        else
        {
            median = Statistics.Median(samples);
            p95 = Statistics.Percentile(samples, 95);
            max = samples.Max();
            if (median > medianBudgetMilliseconds)
            {
                failures.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The median startup was {median:F0} ms, over the {medianBudgetMilliseconds:F0} ms budget."));
            }

            if (p95 > p95BudgetMilliseconds)
            {
                failures.Add(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The p95 startup was {p95:F0} ms, over the {p95BudgetMilliseconds:F0} ms budget."));
            }
        }

        return new StartupSummary(
            runs,
            warmupRuns,
            median,
            p95,
            max,
            samples,
            timedOutRuns,
            failures);
    }
}

internal static class StartupCheck
{
    public static async Task<int> RunAsync(Options options, CancellationToken cancellationToken)
    {
        var executable = Path.GetFullPath(options.Required("exe"));
        if (!File.Exists(executable))
        {
            throw new ArgumentException($"The executable '{executable}' does not exist.");
        }

        var runs = options.OptionalInt("runs", 10);
        var warmupRuns = options.OptionalInt("warmup-runs", 1);
        var timeout = TimeSpan.FromSeconds(options.OptionalDouble("timeout-seconds", 60));
        var medianBudget = options.OptionalDouble(
            "budget-median-ms",
            ReleaseBudgets.StartupMedianMilliseconds);
        var p95Budget = options.OptionalDouble(
            "budget-p95-ms",
            ReleaseBudgets.StartupP95Milliseconds);
        if (runs <= 0 || warmupRuns < 0 || runs <= warmupRuns)
        {
            throw new ArgumentException("--runs must be greater than a non-negative --warmup-runs.");
        }

        if (timeout <= TimeSpan.Zero || medianBudget <= 0 || p95Budget <= 0)
        {
            throw new ArgumentException("The timeout and startup budgets must be positive.");
        }

        Console.Out.WriteLine(
            "Measuring process start to main window, with plugins disabled and an isolated data directory.");
        var samples = new List<double>();
        var timedOutRuns = new List<int>();
        for (var run = 1; run <= runs; run++)
        {
            var (milliseconds, appeared) = await MeasureAsync(
                executable,
                timeout,
                cancellationToken).ConfigureAwait(false);
            var isWarmup = run <= warmupRuns;
            if (!appeared)
            {
                timedOutRuns.Add(run);
                Console.Out.WriteLine($"Run {run}: no window within {timeout.TotalSeconds:F0} s.");
                continue;
            }

            if (!isWarmup)
            {
                samples.Add(milliseconds);
            }

            Console.Out.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Run {run}: {milliseconds:F0} ms{(isWarmup ? " (warmup)" : string.Empty)}."));
        }

        var summary = StartupAnalyzer.Analyze(
            samples,
            timedOutRuns,
            runs,
            warmupRuns,
            medianBudget,
            p95Budget);
        if (samples.Count > 0)
        {
            Console.Out.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Median {summary.MedianMilliseconds:F0} ms (budget {medianBudget:F0} ms); " +
                    $"p95 {summary.P95Milliseconds:F0} ms (budget {p95Budget:F0} ms); " +
                    $"max {summary.MaxMilliseconds:F0} ms."));
        }

        Reports.WriteSummary(new CheckResult(summary.Failures), "Startup budget");
        Reports.Write(
            options.Optional("json"),
            new
            {
                command = "startup",
                timestampUtc = DateTimeOffset.UtcNow,
                label = options.Optional("label"),
                passed = summary.Passed,
                environment = ReportEnvironment.Current,
                executable,
                runs,
                warmupRuns,
                timeoutSeconds = timeout.TotalSeconds,
                samplesMilliseconds = summary.Samples,
                timedOutRuns = summary.TimedOutRuns,
                medianMilliseconds = summary.MedianMilliseconds,
                p95Milliseconds = summary.P95Milliseconds,
                maxMilliseconds = summary.MaxMilliseconds,
                medianBudgetMilliseconds = medianBudget,
                p95BudgetMilliseconds = p95Budget,
                failures = summary.Failures,
            });
        return summary.Passed ? 0 : 1;
    }

    private static async Task<(double Milliseconds, bool Appeared)> MeasureAsync(
        string executable,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "droiddeck-releasecheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--no-plugins");

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"The process '{executable}' could not be started.");
        try
        {
            var appeared = await WaitForMainWindowAsync(process, timeout, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            return (stopwatch.Elapsed.TotalMilliseconds, appeared);
        }
        finally
        {
            await CloseAsync(process, cancellationToken).ConfigureAwait(false);
            DeleteDataDirectory(dataDirectory);
        }
    }

    private static async Task<bool> WaitForMainWindowAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
            {
                return false;
            }

            if (process.MainWindowHandle != IntPtr.Zero)
            {
                return true;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task CloseAsync(Process process, CancellationToken cancellationToken)
    {
        if (!process.HasExited)
        {
            _ = process.CloseMainWindow();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
        }
    }

    private static void DeleteDataDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is harmless; the next run uses a fresh one.
        }
    }
}
