using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class SettingsValuePolicyTests
{
    [Fact]
    public void PolicyCoversEveryGuardedIntegerSetting()
    {
        string[] expectedPaths =
        [
            SettingsPaths.PerformanceSampleIntervalMs,
            SettingsPaths.PerformanceMaxSamples,
            SettingsPaths.InspectorMaxDepth,
            SettingsPaths.ScreenWarnAtPercent,
            SettingsPaths.ProjectScanDepth,
            SettingsPaths.RunMaxConcurrentDevices,
            SettingsPaths.SessionRetentionDays,
        ];

        Assert.Equal(
            expectedPaths,
            SettingsValuePolicy.Ranges.Select(static range => range.Path));
    }

    [Theory]
    [InlineData(
        SettingsPaths.PerformanceSampleIntervalMs,
        PerformanceSettings.MinimumSampleIntervalMs,
        PerformanceSettings.MaximumSampleIntervalMs)]
    [InlineData(
        SettingsPaths.PerformanceMaxSamples,
        PerformanceSettings.MinimumSampleCount,
        PerformanceSettings.MaximumSampleCount)]
    [InlineData(
        SettingsPaths.InspectorMaxDepth,
        InspectorSettings.MinimumDepth,
        InspectorSettings.MaximumDepth)]
    [InlineData(SettingsPaths.ScreenWarnAtPercent, 1, 100)]
    [InlineData(SettingsPaths.ProjectScanDepth, 1, 10)]
    [InlineData(SettingsPaths.RunMaxConcurrentDevices, 1, 32)]
    [InlineData(SettingsPaths.SessionRetentionDays, 0, 3_650)]
    public void EveryRowExposesItsDocumentedRange(string path, int expectedMinimum, int expectedMaximum)
    {
        Assert.Contains(
            SettingsValuePolicy.Ranges,
            range => range.Path == path &&
                range.Minimum == expectedMinimum &&
                range.Maximum == expectedMaximum);

        Assert.True(SettingsValuePolicy.TryGetRange(path, out var minimum, out var maximum));
        Assert.Equal(expectedMinimum, minimum);
        Assert.Equal(expectedMaximum, maximum);
    }

    [Theory]
    [InlineData(
        SettingsPaths.PerformanceSampleIntervalMs,
        PerformanceSettings.MinimumSampleIntervalMs,
        PerformanceSettings.MaximumSampleIntervalMs)]
    [InlineData(
        SettingsPaths.PerformanceMaxSamples,
        PerformanceSettings.MinimumSampleCount,
        PerformanceSettings.MaximumSampleCount)]
    [InlineData(
        SettingsPaths.InspectorMaxDepth,
        InspectorSettings.MinimumDepth,
        InspectorSettings.MaximumDepth)]
    [InlineData(SettingsPaths.ScreenWarnAtPercent, 1, 100)]
    [InlineData(SettingsPaths.ProjectScanDepth, 1, 10)]
    [InlineData(SettingsPaths.RunMaxConcurrentDevices, 1, 32)]
    [InlineData(SettingsPaths.SessionRetentionDays, 0, 3_650)]
    public void IsInRangeAcceptsTheEndpointsAndRejectsTheNeighbors(string path, int minimum, int maximum)
    {
        Assert.True(SettingsValuePolicy.IsInRange(path, minimum));
        Assert.True(SettingsValuePolicy.IsInRange(path, maximum));
        Assert.False(SettingsValuePolicy.IsInRange(path, minimum - 1));
        Assert.False(SettingsValuePolicy.IsInRange(path, maximum + 1));
    }

    [Theory]
    [InlineData(
        SettingsPaths.PerformanceSampleIntervalMs,
        PerformanceSettings.MinimumSampleIntervalMs,
        PerformanceSettings.MaximumSampleIntervalMs)]
    [InlineData(
        SettingsPaths.PerformanceMaxSamples,
        PerformanceSettings.MinimumSampleCount,
        PerformanceSettings.MaximumSampleCount)]
    [InlineData(
        SettingsPaths.InspectorMaxDepth,
        InspectorSettings.MinimumDepth,
        InspectorSettings.MaximumDepth)]
    [InlineData(SettingsPaths.ScreenWarnAtPercent, 1, 100)]
    [InlineData(SettingsPaths.ProjectScanDepth, 1, 10)]
    [InlineData(SettingsPaths.RunMaxConcurrentDevices, 1, 32)]
    [InlineData(SettingsPaths.SessionRetentionDays, 0, 3_650)]
    public void ClampUsesTheRowsEndpoints(string path, int minimum, int maximum)
    {
        Assert.Equal(minimum, SettingsValuePolicy.Clamp(path, minimum - 1));
        Assert.Equal(maximum, SettingsValuePolicy.Clamp(path, maximum + 1));
        Assert.Equal(minimum, SettingsValuePolicy.Clamp(path, minimum));
        Assert.Equal(maximum, SettingsValuePolicy.Clamp(path, maximum));

        var middle = minimum + ((maximum - minimum) / 2);
        Assert.Equal(middle, SettingsValuePolicy.Clamp(path, middle));
    }

    [Fact]
    public void RetentionIsZeroBasedBecauseZeroMeansNoAgeLimit()
    {
        Assert.True(SettingsValuePolicy.TryGetRange(SettingsPaths.SessionRetentionDays, out var minimum, out var maximum));
        Assert.Equal(0, minimum);
        Assert.Equal(3_650, maximum);
        Assert.True(SettingsValuePolicy.IsInRange(SettingsPaths.SessionRetentionDays, 0));
        Assert.Equal(0, SettingsValuePolicy.Clamp(SettingsPaths.SessionRetentionDays, -30));
        Assert.Equal(3_650, SettingsValuePolicy.Clamp(SettingsPaths.SessionRetentionDays, 9_999));
    }

    [Fact]
    public void UnknownPathHasNoRangeAndCannotBeClamped()
    {
        Assert.False(SettingsValuePolicy.TryGetRange("unknown.setting", out var minimum, out var maximum));
        Assert.Equal(0, minimum);
        Assert.Equal(0, maximum);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SettingsValuePolicy.Clamp("unknown.setting", 1));
        Assert.Equal("path", exception.ParamName);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => SettingsValuePolicy.IsInRange("unknown.setting", 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankPathsAreRejected(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => SettingsValuePolicy.TryGetRange(path!, out _, out _));
        Assert.ThrowsAny<ArgumentException>(() => SettingsValuePolicy.Clamp(path!, 1));
        Assert.ThrowsAny<ArgumentException>(() => SettingsValuePolicy.IsInRange(path!, 1));
    }
}
