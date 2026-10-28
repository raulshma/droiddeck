using System.Globalization;
using System.Text;
using DroidDeck.Core.Screen;

namespace DroidDeck.Screen;

/// <summary>Builds deterministic scrcpy command lines for mirroring and recording.</summary>
public static class ScrcpyCommandBuilder
{
    /// <summary>Builds the arguments for an interactive mirror.</summary>
    public static IReadOnlyList<string> BuildMirrorArguments(
        string serial,
        ScreenStartOptions options,
        string? windowTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string> { "-s", serial };
        AddCommonArguments(arguments, options);

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            arguments.Add("--window-title=" + windowTitle);
        }

        if (options.HostingMode == ScreenHostingMode.DockedCompanion && options.Bounds is { IsValid: true } bounds)
        {
            arguments.Add("--window-x=" + bounds.X.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--window-y=" + bounds.Y.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--window-width=" + bounds.Width.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--window-height=" + bounds.Height.ToString(CultureInfo.InvariantCulture));
            arguments.Add("--window-borderless");
        }

        // scrcpy syncs the clipboard by default and only exposes the negative flag.
        if (!options.ClipboardSync)
        {
            arguments.Add("--no-clipboard-autosync");
        }

        if (!options.Audio)
        {
            arguments.Add("--no-audio");
        }

        return arguments;
    }

    /// <summary>
    /// Builds the arguments for a headless recorder. A separate scrcpy process means
    /// starting or stopping a recording never flickers the interactive mirror.
    /// </summary>
    public static IReadOnlyList<string> BuildRecorderArguments(
        string serial,
        ScreenStartOptions options,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        // A window is kept (but hidden off-screen) so the recorder can be asked to
        // close gracefully, which flushes the muxer and leaves a playable file.
        var arguments = new List<string> { "-s", serial, "--no-playback", "--no-control" };
        AddCommonArguments(arguments, options, includeStayAwake: false);
        arguments.Add("--no-audio");
        arguments.Add("--window-title=DroidDeck recording - " + serial);
        arguments.Add("--window-x=-32000");
        arguments.Add("--window-y=-32000");
        arguments.Add("--window-width=1");
        arguments.Add("--window-height=1");
        arguments.Add("--window-borderless");
        arguments.Add("--record=" + outputPath);
        arguments.Add("--record-format=mkv");
        return arguments;
    }

    /// <summary>Formats a command line for display and diagnostics.</summary>
    public static string ToCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder(Quote(executable));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static void AddCommonArguments(
        List<string> arguments,
        ScreenStartOptions options,
        bool includeStayAwake = true)
    {
        if (options.MaxSize is { } maxSize)
        {
            arguments.Add("--max-size=" + Math.Max(1, maxSize).ToString(CultureInfo.InvariantCulture));
        }

        if (options.MaxFps is { } maxFps)
        {
            arguments.Add("--max-fps=" + Math.Max(1, maxFps).ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(options.VideoBitRate))
        {
            arguments.Add("--video-bit-rate=" + options.VideoBitRate.Trim());
        }

        if (includeStayAwake && options.StayAwake)
        {
            arguments.Add("--stay-awake");
        }
    }

    private static string Quote(string value) =>
        value.Length > 0 && value.All(static character => !char.IsWhiteSpace(character) && character != '"')
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
