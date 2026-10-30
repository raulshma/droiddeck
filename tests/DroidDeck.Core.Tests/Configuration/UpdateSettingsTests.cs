using DroidDeck.Core.Configuration;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class UpdateSettingsTests
{
    [Fact]
    public async Task ManualUpdateChecksAreOffByDefault()
    {
        using var temp = new TempDirectory();
        using var service = new ConfigurationService(new AppPaths(temp.Combine("data")));

        await service.InitializeAsync();

        Assert.False(service.GetValue<bool>(SettingsPaths.UpdatesManualCheckEnabled));
        Assert.False(service.Current.Settings.Updates.ManualCheckEnabled);
    }

    [Fact]
    public async Task ManualUpdateChecksPersistAfterRestart()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        using (var service = new ConfigurationService(paths))
        {
            await service.InitializeAsync();
            await service.SetAsync(SettingsPaths.UpdatesManualCheckEnabled, true);
        }

        using var restarted = new ConfigurationService(paths);
        await restarted.InitializeAsync();

        Assert.True(restarted.GetValue<bool>(SettingsPaths.UpdatesManualCheckEnabled));
    }
}
