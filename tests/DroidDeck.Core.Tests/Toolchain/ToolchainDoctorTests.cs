using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolchain;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Toolchain;

public sealed class ToolchainDoctorTests
{
    [Fact]
    public async Task ReportsEveryToolWhenNothingIsInstalled()
    {
        using var temp = new TempDirectory();
        using var configuration = await CreateConfigurationAsync(temp);
        using var doctor = CreateDoctor(temp, configuration);

        var snapshot = await doctor.CheckAsync();

        Assert.Equal(Enum.GetValues<ToolKind>().Length, snapshot.Tools.Count);
        Assert.All(snapshot.Tools, tool =>
        {
            Assert.False(tool.IsAvailable, tool.DisplayName);
            Assert.Equal(ToolDetectionSource.Missing, tool.Source);
            Assert.False(string.IsNullOrWhiteSpace(tool.Guidance));
        });
        Assert.Null(snapshot.AndroidSdkPath);
        Assert.All(Enum.GetValues<ToolKind>(), kind => Assert.False(snapshot.IsAvailable(kind)));
    }

    [Fact]
    public async Task FindsAdbOnPathAndParsesItsVersion()
    {
        using var temp = new TempDirectory();
        var bin = temp.CreateDirectory("bin");
        ToolchainTestEnvironment.WriteScript(
            temp,
            "bin/adb.cmd",
            ["Android Debug Bridge version 1.0.41", "Version 35.0.2-droiddeck"]);
        using var configuration = await CreateConfigurationAsync(temp);
        using var doctor = CreateDoctor(temp, configuration, [bin]);

        var snapshot = await doctor.CheckAsync();
        var adb = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.Adb));

        Assert.True(adb.IsAvailable);
        Assert.Equal(ToolDetectionSource.Automatic, adb.Source);
        Assert.Equal("35.0.2-droiddeck", adb.Version);
        Assert.EndsWith("adb.cmd", adb.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HonorsAnExecutableOverride()
    {
        using var temp = new TempDirectory();
        var adbPath = ToolchainTestEnvironment.WriteScript(temp, "custom/adb.cmd", "Version 99.0.0-custom");
        using var configuration = await CreateConfigurationAsync(temp);
        await configuration.SetAsync("tools.adbPath", adbPath);
        using var doctor = CreateDoctor(temp, configuration);

        var snapshot = await doctor.CheckAsync();
        var adb = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.Adb));

        Assert.True(adb.IsAvailable);
        Assert.Equal(ToolDetectionSource.Override, adb.Source);
        Assert.Equal("99.0.0-custom", adb.Version);
        Assert.Equal(adbPath, adb.Path, ignoreCase: true);
    }

    [Fact]
    public async Task ReportsAnUnreadableOverrideWithGuidance()
    {
        using var temp = new TempDirectory();
        using var configuration = await CreateConfigurationAsync(temp);
        await configuration.SetAsync("tools.adbPath", temp.Combine("missing", "adb.exe"));
        using var doctor = CreateDoctor(temp, configuration);

        var snapshot = await doctor.CheckAsync();
        var adb = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.Adb));

        Assert.False(adb.IsAvailable);
        Assert.Equal(ToolDetectionSource.Override, adb.Source);
        Assert.False(string.IsNullOrWhiteSpace(adb.Error));
        Assert.Contains("Settings > Tools", adb.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoversToolsBeneathTheAndroidSdk()
    {
        using var temp = new TempDirectory();
        var sdk = temp.CreateDirectory("sdk");
        var expectedAdb = ToolchainTestEnvironment.WriteScript(
            temp,
            "sdk/platform-tools/adb.cmd",
            "Version 36.0.0-sdk");
        ToolchainTestEnvironment.WriteScript(
            temp,
            "sdk/build-tools/34.0.0/aapt2.cmd",
            "Android Asset Packaging Tool (aapt) 2.19-11111111");
        ToolchainTestEnvironment.WriteScript(
            temp,
            "sdk/build-tools/35.0.0/aapt2.cmd",
            "Android Asset Packaging Tool (aapt) 2.20-22222222");

        using var configuration = await CreateConfigurationAsync(temp);
        await configuration.SetAsync("tools.androidSdkPath", sdk);
        using var doctor = CreateDoctor(temp, configuration);

        var snapshot = await doctor.CheckAsync();
        var sdkInfo = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.AndroidSdk));
        var adb = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.Adb));
        var aapt2 = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.Aapt2));

        Assert.True(sdkInfo.IsAvailable);
        Assert.Equal(sdk, snapshot.AndroidSdkPath, ignoreCase: true);
        Assert.True(adb.IsAvailable);
        Assert.Equal(expectedAdb, adb.Path, ignoreCase: true);
        Assert.True(aapt2.IsAvailable);
        Assert.Equal("2.20-22222222", aapt2.Version);
        Assert.EndsWith("35.0.0/aapt2.cmd", aapt2.Path!.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvesTheSdkFromAndroidHome()
    {
        using var temp = new TempDirectory();
        var sdk = temp.CreateDirectory("android-sdk");
        using var configuration = await CreateConfigurationAsync(temp);
        var environment = ToolchainTestEnvironment.Create(
            temp,
            variables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ANDROID_HOME"] = sdk,
            });
        using var doctor = new ToolchainDoctor(
            new AppPaths(temp.Combine("data")),
            configuration,
            new ProcessRunner(),
            environment);

        var snapshot = await doctor.CheckAsync();
        var info = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.AndroidSdk));

        Assert.True(info.IsAvailable);
        Assert.Equal(sdk, info.Path, ignoreCase: true);
        Assert.Equal(ToolDetectionSource.Automatic, info.Source);
    }

    [Fact]
    public async Task RejectsAnInvalidJavaHome()
    {
        using var temp = new TempDirectory();
        using var configuration = await CreateConfigurationAsync(temp);
        await configuration.SetAsync("tools.javaHome", temp.Combine("missing-jdk"));
        using var doctor = CreateDoctor(temp, configuration);

        var snapshot = await doctor.CheckAsync();
        var java = Assert.IsType<ToolInfo>(snapshot.Get(ToolKind.Java));

        Assert.False(java.IsAvailable);
        Assert.Equal(ToolDetectionSource.Override, java.Source);
        Assert.False(string.IsNullOrWhiteSpace(java.Error));
    }

    [Fact]
    public async Task CachesAndReloadsTheSnapshot()
    {
        using var temp = new TempDirectory();
        var bin = temp.CreateDirectory("bin");
        ToolchainTestEnvironment.WriteScript(temp, "bin/adb.cmd", "Version 35.0.2-cached");
        using var configuration = await CreateConfigurationAsync(temp);
        var paths = new AppPaths(temp.Combine("data"));
        using var doctor = new ToolchainDoctor(
            paths,
            configuration,
            new ProcessRunner(),
            ToolchainTestEnvironment.Create(temp, [bin]));

        var checkedSnapshot = await doctor.CheckAsync();
        Assert.True(File.Exists(paths.ToolchainCacheFile));

        using var coldDoctor = new ToolchainDoctor(
            paths,
            configuration,
            new ProcessRunner(),
            ToolchainTestEnvironment.Create(temp));
        var cached = await coldDoctor.LoadCachedAsync();

        Assert.NotNull(cached);
        Assert.Equal(checkedSnapshot.CheckedAt, cached.CheckedAt);
        Assert.Equal("35.0.2-cached", cached.Get(ToolKind.Adb)?.Version);
        Assert.True(cached.Get(ToolKind.Adb)?.IsAvailable == true);
    }

    [Fact]
    public async Task LoadsNoCacheWhenTheFileIsCorrupt()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.ToolchainCacheFile, "{ not json");
        using var configuration = await CreateConfigurationAsync(temp);
        using var doctor = CreateDoctor(temp, configuration);

        Assert.Null(await doctor.LoadCachedAsync());
    }

    private static async Task<ConfigurationService> CreateConfigurationAsync(TempDirectory temp)
    {
        var configuration = new ConfigurationService(new AppPaths(temp.Combine("data")));
        await configuration.InitializeAsync();
        return configuration;
    }

    private static ToolchainDoctor CreateDoctor(
        TempDirectory temp,
        IConfigurationService configuration,
        IReadOnlyList<string>? pathDirectories = null)
    {
        return new ToolchainDoctor(
            new AppPaths(temp.Combine("data")),
            configuration,
            new ProcessRunner(),
            ToolchainTestEnvironment.Create(temp, pathDirectories));
    }
}
