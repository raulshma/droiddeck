using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class PerformanceSettingsTests
{
    [Fact]
    public void SamplingDefaultsToOneSecondAndSixtySamples()
    {
        var settings = new AppSettings();

        Assert.Equal(1_000, settings.Performance.SampleIntervalMs);
        Assert.Equal(60, settings.Performance.MaxSamples);
    }

    [Fact]
    public void SampleIntervalIsClampedToAUsableRange()
    {
        var settings = new PerformanceSettings { SampleIntervalMs = 5 };

        Assert.Equal(250, settings.EffectiveSampleIntervalMs);

        settings.SampleIntervalMs = 120_000;

        Assert.Equal(60_000, settings.EffectiveSampleIntervalMs);
    }

    [Fact]
    public void ClampHelpersUseTheSupportedRanges()
    {
        Assert.Equal(250, PerformanceSettings.ClampSampleInterval(1));
        Assert.Equal(60_000, PerformanceSettings.ClampSampleInterval(120_000));
        Assert.Equal(10, PerformanceSettings.ClampSampleCount(1));
        Assert.Equal(3_600, PerformanceSettings.ClampSampleCount(5_000));
    }

    [Fact]
    public void SettingsPathsExposePerformanceKeys()
    {
        Assert.Equal("performance.sampleIntervalMs", SettingsPaths.PerformanceSampleIntervalMs);
        Assert.Equal("performance.maxSamples", SettingsPaths.PerformanceMaxSamples);
    }
}
