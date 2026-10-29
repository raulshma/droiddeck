using DroidDeck.Core.Configuration;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class InspectorSettingsTests
{
    [Fact]
    public void DefaultsHideInvisibleNodesAndLimitDepth()
    {
        var settings = new AppSettings();

        Assert.Equal(12, settings.Inspector.MaxDepth);
        Assert.False(settings.Inspector.IncludeInvisible);
        Assert.Equal(12, settings.Inspector.EffectiveMaxDepth);
    }

    [Fact]
    public void DepthIsClampedToAUsableRange()
    {
        var settings = new InspectorSettings { MaxDepth = 0 };
        Assert.Equal(1, settings.EffectiveMaxDepth);

        settings.MaxDepth = 500;
        Assert.Equal(100, settings.EffectiveMaxDepth);
    }

    [Fact]
    public void SettingsPathsExposeInspectorKeys()
    {
        Assert.Equal("inspector.maxDepth", SettingsPaths.InspectorMaxDepth);
        Assert.Equal("inspector.includeInvisible", SettingsPaths.InspectorIncludeInvisible);
    }
}
