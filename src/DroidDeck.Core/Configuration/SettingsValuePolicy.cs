namespace DroidDeck.Core.Configuration;

/// <summary>One whole-number setting and the values DroidDeck supports for it.</summary>
/// <param name="Path">The dotted configuration path.</param>
/// <param name="Minimum">The smallest supported value.</param>
/// <param name="Maximum">The largest supported value.</param>
public readonly record struct SettingsValueRange(string Path, int Minimum, int Maximum)
{
    /// <summary>Returns whether a value lies inside the supported range.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is between the endpoints inclusively.</returns>
    public bool Contains(int value) => value >= Minimum && value <= Maximum;

    /// <summary>Moves a value to the nearest supported endpoint.</summary>
    /// <param name="value">The value to clamp.</param>
    /// <returns>The value when it is supported, otherwise <see cref="Minimum"/> or <see cref="Maximum"/>.</returns>
    public int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);
}

/// <summary>
/// The single source of truth for the supported range of every whole-number setting.
/// The settings page clamps input with this table and the configuration health panel
/// reports and repairs values outside it, so both surfaces always agree.
/// </summary>
public static class SettingsValuePolicy
{
    /// <summary>Gets every integer setting that has a supported range.</summary>
    public static IReadOnlyList<SettingsValueRange> Ranges { get; } =
    [
        new(
            SettingsPaths.PerformanceSampleIntervalMs,
            PerformanceSettings.MinimumSampleIntervalMs,
            PerformanceSettings.MaximumSampleIntervalMs),
        new(
            SettingsPaths.PerformanceMaxSamples,
            PerformanceSettings.MinimumSampleCount,
            PerformanceSettings.MaximumSampleCount),
        new(
            SettingsPaths.InspectorMaxDepth,
            InspectorSettings.MinimumDepth,
            InspectorSettings.MaximumDepth),
        new(SettingsPaths.ScreenWarnAtPercent, 1, 100),
        new(SettingsPaths.ProjectScanDepth, 1, 10),
        new(SettingsPaths.RunMaxConcurrentDevices, 1, 32),
        new(SettingsPaths.SessionRetentionDays, 0, 3_650),
    ];

    private static readonly Dictionary<string, SettingsValueRange> RangesByPath =
        Ranges.ToDictionary(static range => range.Path, StringComparer.Ordinal);

    /// <summary>Looks up the supported range of a setting.</summary>
    /// <param name="path">The dotted configuration path.</param>
    /// <param name="min">The smallest supported value, or 0 when the path is unknown.</param>
    /// <param name="max">The largest supported value, or 0 when the path is unknown.</param>
    /// <returns><see langword="true"/> when the setting has a supported integer range.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or whitespace.</exception>
    public static bool TryGetRange(string path, out int min, out int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (RangesByPath.TryGetValue(path, out var range))
        {
            min = range.Minimum;
            max = range.Maximum;
            return true;
        }

        min = 0;
        max = 0;
        return false;
    }

    /// <summary>Clamps a value to the range supported for the setting.</summary>
    /// <param name="path">The dotted configuration path.</param>
    /// <param name="value">The value to clamp.</param>
    /// <returns>The value when it is supported, otherwise the nearest endpoint.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="path"/> has no supported integer range.</exception>
    public static int Clamp(string path, int value) => GetRange(path).Clamp(value);

    /// <summary>Returns whether a value is inside the range supported for the setting.</summary>
    /// <param name="path">The dotted configuration path.</param>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when the value is supported.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="path"/> has no supported integer range.</exception>
    public static bool IsInRange(string path, int value) => GetRange(path).Contains(value);

    private static SettingsValueRange GetRange(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (RangesByPath.TryGetValue(path, out var range))
        {
            return range;
        }

        throw new ArgumentOutOfRangeException(
            nameof(path),
            path,
            "No integer range is registered for this setting path.");
    }
}
