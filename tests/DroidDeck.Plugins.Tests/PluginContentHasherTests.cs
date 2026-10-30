using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginContentHasherTests
{
    [Fact]
    public async Task TheSameFilesProduceTheSameHash()
    {
        using var temp = new TempDirectory();
        var directory = StagePluginDirectory(temp);

        var first = await PluginContentHasher.ComputeAsync(directory);
        var second = await PluginContentHasher.ComputeAsync(directory);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public async Task ChangingAPluginFileChangesTheHash()
    {
        using var temp = new TempDirectory();
        var directory = StagePluginDirectory(temp);
        var before = await PluginContentHasher.ComputeAsync(directory);

        await File.WriteAllTextAsync(Path.Combine(directory, "helpers.dll"), "changed");
        var after = await PluginContentHasher.ComputeAsync(directory);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task RuntimeDataAndSettingsDoNotChangeTheHash()
    {
        using var temp = new TempDirectory();
        var directory = StagePluginDirectory(temp);
        var before = await PluginContentHasher.ComputeAsync(directory);

        var dataDirectory = Directory.CreateDirectory(Path.Combine(directory, "data"));
        await File.WriteAllTextAsync(Path.Combine(dataDirectory.FullName, "cache.bin"), "runtime state");
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"theme\":\"dark\"}");
        var after = await PluginContentHasher.ComputeAsync(directory);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RenamingAPluginFileChangesTheHash()
    {
        using var temp = new TempDirectory();
        var directory = StagePluginDirectory(temp);
        var before = await PluginContentHasher.ComputeAsync(directory);

        File.Move(Path.Combine(directory, "helpers.dll"), Path.Combine(directory, "helpers2.dll"));
        var after = await PluginContentHasher.ComputeAsync(directory);

        Assert.NotEqual(before, after);
    }

    private static string StagePluginDirectory(TempDirectory temp)
    {
        var directory = temp.CreateDirectory("plugins", "com.example.sample");
        File.WriteAllText(Path.Combine(directory, "plugin.json"), "{\"id\":\"com.example.sample\"}");
        File.WriteAllText(Path.Combine(directory, "Sample.dll"), "assembly bytes");
        File.WriteAllText(Path.Combine(directory, "helpers.dll"), "helper bytes");
        return directory;
    }
}
