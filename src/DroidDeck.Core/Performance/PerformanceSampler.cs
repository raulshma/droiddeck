using DroidDeck.Core.Toolbox;

namespace DroidDeck.Core.Performance;

/// <summary>
/// Captures performance points for one device by running dumpsys commands through
/// the toolbox. All work is asynchronous so callers can keep the UI thread free.
/// </summary>
public sealed class PerformanceSampler
{
    private readonly IDeviceToolboxService _toolbox;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a sampler.</summary>
    public PerformanceSampler(IDeviceToolboxService toolbox, TimeProvider? timeProvider = null)
    {
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Captures one point. Frame rates are computed against
    /// <paramref name="previousFrames"/> and <paramref name="elapsed"/> when supplied.
    /// </summary>
    public async Task<PerformanceSample> CaptureAsync(
        string serial,
        string? package = null,
        PerformanceFrameStats? previousFrames = null,
        TimeSpan? elapsed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var warnings = new List<string>();

        var cpu = await TryRunShellAsync(serial, "dumpsys cpuinfo", warnings, cancellationToken).ConfigureAwait(false);
        double? memory = null;
        PerformanceFrameStats? frames = null;
        if (!string.IsNullOrWhiteSpace(package))
        {
            var meminfo = await TryRunShellAsync(
                serial,
                $"dumpsys meminfo {package}",
                warnings,
                cancellationToken).ConfigureAwait(false);
            memory = meminfo is null ? null : PerformanceSampleParser.ParseMemoryMegabytes(meminfo);

            var gfxinfo = await TryRunShellAsync(
                serial,
                $"dumpsys gfxinfo {package}",
                warnings,
                cancellationToken).ConfigureAwait(false);
            frames = gfxinfo is null ? null : PerformanceSampleParser.ParseFrameStats(gfxinfo);
        }

        var batteryOutput = await TryRunShellAsync(
            serial,
            "dumpsys battery",
            warnings,
            cancellationToken).ConfigureAwait(false);
        var battery = batteryOutput is null
            ? new PerformanceBatteryState()
            : PerformanceSampleParser.ParseBattery(batteryOutput);

        var rates = frames is null
            ? new PerformanceFrameRates()
            : PerformanceSampleParser.ComputeFrameRates(
                previousFrames,
                frames,
                elapsed ?? TimeSpan.Zero);

        return new PerformanceSample
        {
            Timestamp = _timeProvider.GetUtcNow(),
            CpuPercent = cpu is null ? null : PerformanceSampleParser.ParseCpuPercent(cpu, package),
            MemoryMegabytes = memory,
            Fps = rates.Fps,
            JankPercent = rates.JankPercent,
            BatteryPercent = battery.Level,
            BatteryTemperatureCelsius = battery.TemperatureCelsius,
            FrameStats = frames,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Samples until cancellation. The first point is captured immediately and later
    /// points start on a fixed cadence of one <paramref name="interval"/>, so the time
    /// spent running the device commands does not stretch the sampling period.
    /// </summary>
    public async Task RunAsync(
        string serial,
        string? package,
        TimeSpan interval,
        Func<PerformanceSample, Task> onSample,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The sampling interval must be positive.");
        }

        PerformanceFrameStats? previousFrames = null;
        DateTimeOffset? previousAt = null;
        DateTimeOffset? nextAt = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            PerformanceSample sample;
            try
            {
                var elapsed = previousAt is { } at ? _timeProvider.GetUtcNow() - at : (TimeSpan?)null;
                sample = await CaptureAsync(
                    serial,
                    package,
                    previousFrames,
                    elapsed,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await onSample(sample).ConfigureAwait(false);
            previousFrames = sample.FrameStats;
            previousAt = sample.Timestamp;

            var now = _timeProvider.GetUtcNow();
            var scheduled = (nextAt ?? now) + interval;
            if (scheduled <= now)
            {
                scheduled = now + interval;
            }

            nextAt = scheduled;
            try
            {
                var wait = scheduled - now;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<string?> TryRunShellAsync(
        string serial,
        string command,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _toolbox
                .RunShellAsync(serial, command, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (result.Succeeded)
            {
                return result.Output;
            }

            warnings.Add($"{command}: {result.Message}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            warnings.Add($"{command}: {exception.Message}");
        }

        return null;
    }
}
