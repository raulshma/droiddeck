using DroidDeck.Core.Diagnostics;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Tests.Diagnostics;

public sealed class RollingFileLoggerProviderTests
{
    [Fact]
    public void Logger_WritesRedactedLinesToTheDailyFile()
    {
        using var temp = new TempDirectory();
        using var provider = new RollingFileLoggerProvider(temp.Path, "app", 14, "SESSION");
        var logger = provider.CreateLogger("DroidDeck.Tests");

        logger.LogError("token=super-secret and Bearer abc.def.ghi");

        var file = Assert.Single(Directory.EnumerateFiles(temp.Path, "app-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("[Error]", text, StringComparison.Ordinal);
        Assert.Contains("DroidDeck.Tests", text, StringComparison.Ordinal);
        Assert.Contains("SESSION", text, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DeletesFilesOlderThanRetention()
    {
        using var temp = new TempDirectory();
        var oldFile = temp.WriteFile("app-20200101.log", "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-30));
        var recentFile = temp.WriteFile("app-old-recent.log", "recent");

        using var provider = new RollingFileLoggerProvider(temp.Path, "app", 14);

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
    }

    [Fact]
    public void Logger_AfterDisposeDoesNotWriteOrThrow()
    {
        using var temp = new TempDirectory();
        var provider = new RollingFileLoggerProvider(temp.Path);
        var logger = provider.CreateLogger("Tests");
        provider.Dispose();

        logger.LogInformation("after dispose");

        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.log"));
    }
}
