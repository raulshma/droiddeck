namespace DroidDeck.Core.Performance;

/// <summary>One point in a performance sampling session.</summary>
public sealed record PerformanceSample
{
    /// <summary>Gets when the sample was captured.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Gets the application CPU usage percentage, or the device total when the app is not listed.</summary>
    public double? CpuPercent { get; init; }

    /// <summary>Gets the application memory usage in megabytes.</summary>
    public double? MemoryMegabytes { get; init; }

    /// <summary>Gets the frame rate computed from the delta between samples.</summary>
    public double? Fps { get; init; }

    /// <summary>Gets the percentage of frames that missed the deadline.</summary>
    public double? JankPercent { get; init; }

    /// <summary>Gets the battery charge percentage.</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>Gets the battery temperature in degrees Celsius.</summary>
    public double? BatteryTemperatureCelsius { get; init; }

    /// <summary>Gets the raw cumulative frame counters used to compute the next sample.</summary>
    public PerformanceFrameStats? FrameStats { get; init; }

    /// <summary>Gets non-fatal collection problems for this sample.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>Cumulative frame counters read from <c>dumpsys gfxinfo</c>.</summary>
public sealed record PerformanceFrameStats
{
    /// <summary>Gets the total frames rendered since the counters were reset.</summary>
    public int TotalFrames { get; init; }

    /// <summary>Gets the number of janky frames since the counters were reset.</summary>
    public int JankyFrames { get; init; }

    /// <summary>Gets the cumulative jank percentage reported by the device.</summary>
    public double? JankPercent { get; init; }
}

/// <summary>Battery level and temperature.</summary>
public sealed record PerformanceBatteryState
{
    /// <summary>Gets the charge level percentage.</summary>
    public int? Level { get; init; }

    /// <summary>Gets the temperature in degrees Celsius.</summary>
    public double? TemperatureCelsius { get; init; }
}

/// <summary>Frame rates derived from two cumulative counter snapshots.</summary>
public sealed record PerformanceFrameRates
{
    /// <summary>Gets the frames per second, or <see langword="null"/> for the first sample.</summary>
    public double? Fps { get; init; }

    /// <summary>Gets the jank percentage.</summary>
    public double? JankPercent { get; init; }
}

/// <summary>Minimum, average, and maximum for one sampled series.</summary>
public sealed record PerformanceStat
{
    /// <summary>Gets the smallest value, or <see langword="null"/> when the series is empty.</summary>
    public double? Min { get; init; }

    /// <summary>Gets the arithmetic mean, or <see langword="null"/> when the series is empty.</summary>
    public double? Average { get; init; }

    /// <summary>Gets the largest value, or <see langword="null"/> when the series is empty.</summary>
    public double? Max { get; init; }

    /// <summary>Gets how many values contributed to the statistic.</summary>
    public int Count { get; init; }
}

/// <summary>A post-session summary of every collected series.</summary>
public sealed record PerformanceSummary
{
    /// <summary>Gets the number of samples in the session.</summary>
    public int SampleCount { get; init; }

    /// <summary>Gets the wall-clock duration covered by the samples.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets the CPU usage statistics.</summary>
    public PerformanceStat Cpu { get; init; } = new();

    /// <summary>Gets the memory statistics.</summary>
    public PerformanceStat Memory { get; init; } = new();

    /// <summary>Gets the frame rate statistics.</summary>
    public PerformanceStat Fps { get; init; } = new();

    /// <summary>Gets the jank statistics.</summary>
    public PerformanceStat Jank { get; init; } = new();

    /// <summary>Gets the battery percentage statistics.</summary>
    public PerformanceStat Battery { get; init; } = new();

    /// <summary>Computes a summary from sampled points.</summary>
    public static PerformanceSummary FromSamples(IReadOnlyList<PerformanceSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return new PerformanceSummary();
        }

        var ordered = samples.OrderBy(sample => sample.Timestamp).ToArray();
        return new PerformanceSummary
        {
            SampleCount = ordered.Length,
            Duration = ordered[^1].Timestamp - ordered[0].Timestamp,
            Cpu = Summarize(ordered.Select(sample => sample.CpuPercent)),
            Memory = Summarize(ordered.Select(sample => sample.MemoryMegabytes)),
            Fps = Summarize(ordered.Select(sample => sample.Fps)),
            Jank = Summarize(ordered.Select(sample => sample.JankPercent)),
            Battery = Summarize(ordered.Select(sample => sample.BatteryPercent is { } level ? level : (double?)null)),
        };
    }

    private static PerformanceStat Summarize(IEnumerable<double?> values)
    {
        var present = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return present.Length == 0
            ? new PerformanceStat()
            : new PerformanceStat
            {
                Min = present.Min(),
                Average = present.Average(),
                Max = present.Max(),
                Count = present.Length,
            };
    }
}
