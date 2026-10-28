using DroidDeck.Core.Screen;

namespace DroidDeck.Screen.Tests;

public sealed class ScrcpyCommandBuilderTests
{
    [Fact]
    public void BuildMirrorArguments_DockedIncludesBoundsAndStreamOptions()
    {
        var options = new ScreenStartOptions
        {
            HostingMode = ScreenHostingMode.DockedCompanion,
            Bounds = new ScreenBounds(10, 20, 800, 600),
            MaxSize = 1024,
            MaxFps = 60,
            VideoBitRate = "8M",
            StayAwake = true,
            ClipboardSync = true,
            Audio = false,
        };

        var arguments = ScrcpyCommandBuilder.BuildMirrorArguments("emulator-5554", options, "DroidDeck - emulator");

        Assert.Equal("emulator-5554", arguments[1]);
        Assert.Contains("--window-title=DroidDeck - emulator", arguments);
        Assert.Contains("--window-x=10", arguments);
        Assert.Contains("--window-y=20", arguments);
        Assert.Contains("--window-width=800", arguments);
        Assert.Contains("--window-height=600", arguments);
        Assert.Contains("--window-borderless", arguments);
        Assert.Contains("--max-size=1024", arguments);
        Assert.Contains("--max-fps=60", arguments);
        Assert.Contains("--video-bit-rate=8M", arguments);
        Assert.Contains("--stay-awake", arguments);
        Assert.DoesNotContain(arguments, argument =>
            argument.StartsWith("--clipboard", StringComparison.Ordinal));
        Assert.Contains("--no-audio", arguments);
    }

    [Fact]
    public void BuildMirrorArguments_ExternalOmitsBoundsButKeepsPlayback()
    {
        var options = new ScreenStartOptions
        {
            HostingMode = ScreenHostingMode.ExternalWindow,
            Bounds = new ScreenBounds(10, 20, 800, 600),
            ClipboardSync = false,
            Audio = true,
        };

        var arguments = ScrcpyCommandBuilder.BuildMirrorArguments("serial", options);

        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--window-x=", StringComparison.Ordinal));
        Assert.DoesNotContain("--window-borderless", arguments);
        Assert.Contains("--no-clipboard-autosync", arguments);
        Assert.DoesNotContain("--no-audio", arguments);
    }

    [Fact]
    public void BuildRecorderArguments_IsHeadlessAndRecordsMkv()
    {
        var options = new ScreenStartOptions
        {
            MaxSize = 720,
            MaxFps = 30,
            VideoBitRate = "4M",
            StayAwake = true,
        };

        var arguments = ScrcpyCommandBuilder.BuildRecorderArguments(
            "serial",
            options,
            @"C:\recordings\clip.mkv");

        Assert.Contains("--no-playback", arguments);
        Assert.Contains("--no-control", arguments);
        Assert.DoesNotContain("--no-window", arguments);
        Assert.DoesNotContain("--stay-awake", arguments);
        Assert.Contains("--window-x=-32000", arguments);
        Assert.Contains("--window-borderless", arguments);
        Assert.Contains("--no-audio", arguments);
        Assert.Contains(@"--record=C:\recordings\clip.mkv", arguments);
        Assert.Contains("--record-format=mkv", arguments);
        Assert.Contains("--max-size=720", arguments);
        Assert.Contains("--max-fps=30", arguments);
    }

    [Fact]
    public void ToCommandLine_QuotesArgumentsWithSpaces()
    {
        var commandLine = ScrcpyCommandBuilder.ToCommandLine(
            @"C:\Program Files\scrcpy\scrcpy.exe",
            ["-s", "serial", "--window-title=DroidDeck - serial"]);

        Assert.Equal(
            "\"C:\\Program Files\\scrcpy\\scrcpy.exe\" -s serial \"--window-title=DroidDeck - serial\"",
            commandLine);
    }
}
