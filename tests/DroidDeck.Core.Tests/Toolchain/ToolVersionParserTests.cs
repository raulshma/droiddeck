using DroidDeck.Core.Toolchain;

namespace DroidDeck.Core.Tests.Toolchain;

public sealed class ToolVersionParserTests
{
    [Fact]
    public void ParsesAdbBanners()
    {
        var modern = "Android Debug Bridge version 1.0.41\nVersion 35.0.2-12147458\nInstalled as C:\\sdk\\adb.exe";
        var legacy = "Android Debug Bridge version 1.0.39";

        Assert.Equal("35.0.2-12147458", ToolVersionParser.Adb(modern));
        Assert.Equal("1.0.39", ToolVersionParser.Adb(legacy));
        Assert.Null(ToolVersionParser.Adb("no version here"));
    }

    [Fact]
    public void ParsesJavaVersionFromStandardError()
    {
        Assert.Equal(
            "17.0.11",
            ToolVersionParser.Java("openjdk version \"17.0.11\" 2024-04-16\nOpenJDK Runtime Environment"));
        Assert.Equal("1.8.0_392", ToolVersionParser.Java("java version \"1.8.0_392\""));
    }

    [Fact]
    public void ParsesGradleAndFlutterBanners()
    {
        Assert.Equal("8.7", ToolVersionParser.Gradle("\nWelcome to Gradle 8.7!\n\nGradle 8.7\n"));
        Assert.Equal(
            "3.24.1",
            ToolVersionParser.Flutter("Flutter 3.24.1 • channel stable • https://github.com/flutter/flutter"));
    }

    [Fact]
    public void ParsesDartBanner()
    {
        Assert.Equal(
            "3.5.4",
            ToolVersionParser.Dart("Dart SDK version: 3.5.4 (stable) (Tue Sep 24 2024) on \"windows_x64\""));
        Assert.Null(ToolVersionParser.Dart("no version here"));
    }

    [Fact]
    public void ParsesEmulatorScrcpyAndNodeVersions()
    {
        Assert.Equal(
            "35.1.4.0",
            ToolVersionParser.Emulator("Android emulator version 35.1.4.0 (build_id 1234567)"));
        Assert.Equal("2.4", ToolVersionParser.Scrcpy("scrcpy 2.4 <https://github.com/Genymobile/scrcpy>"));
        Assert.Equal("20.11.1", ToolVersionParser.Numeric("v20.11.1"));
        Assert.Equal("10.2.4", ToolVersionParser.Numeric("10.2.4\n"));
    }

    [Fact]
    public void ParsesAapt2AsTheLastTokenOfTheFirstLine()
    {
        Assert.Equal(
            "2.19-11080814",
            ToolVersionParser.LastTokenOfFirstLine("Android Asset Packaging Tool (aapt) 2.19-11080814"));
        Assert.Null(ToolVersionParser.LastTokenOfFirstLine("no version"));
    }
}
