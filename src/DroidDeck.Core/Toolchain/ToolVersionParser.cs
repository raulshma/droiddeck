using System.Text.RegularExpressions;

namespace DroidDeck.Core.Toolchain;

/// <summary>Extracts a display version from a tool's version banner.</summary>
public static partial class ToolVersionParser
{
    /// <summary>Parses <c>adb version</c> output.</summary>
    public static string? Adb(string output) =>
        Match(AdbVersionLine(), output) ?? Match(AdbLegacyLine(), output);

    /// <summary>Parses <c>java -version</c> output, which is written to standard error.</summary>
    public static string? Java(string output) => Match(JavaVersion(), output);

    /// <summary>Parses <c>gradle --version</c> output.</summary>
    public static string? Gradle(string output) => Match(GradleVersion(), output);

    /// <summary>Parses <c>flutter --version</c> output.</summary>
    public static string? Flutter(string output) => Match(FlutterVersion(), output);

    /// <summary>Parses <c>dart --version</c> output.</summary>
    public static string? Dart(string output) => Match(DartVersion(), output);

    /// <summary>Parses <c>emulator -version</c> output.</summary>
    public static string? Emulator(string output) => Match(EmulatorVersion(), output);

    /// <summary>Parses <c>scrcpy --version</c> output.</summary>
    public static string? Scrcpy(string output) => Match(ScrcpyVersion(), output);

    /// <summary>Parses the version line of a node-based tool.</summary>
    public static string? Numeric(string output) => Match(NumericVersion(), output);

    /// <summary>Parses the last token of the first non-empty line, used by aapt2.</summary>
    public static string? LastTokenOfFirstLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var tokens = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var last = tokens[^1];
            return last.Length > 0 && char.IsAsciiDigit(last[0]) ? last.TrimEnd('.') : null;
        }

        return null;
    }

    private static string? Match(Regex regex, string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }

        var match = regex.Match(output);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"(?m)^Version\s+(\S+)")]
    private static partial Regex AdbVersionLine();

    [GeneratedRegex(@"Android Debug Bridge version\s+(\S+)")]
    private static partial Regex AdbLegacyLine();

    [GeneratedRegex(@"version\s+""([^""]+)""")]
    private static partial Regex JavaVersion();

    [GeneratedRegex(@"(?m)^Gradle\s+(\S+)")]
    private static partial Regex GradleVersion();

    [GeneratedRegex(@"(?m)^Flutter\s+(\d+\.\d+\.\d+\S*)")]
    private static partial Regex FlutterVersion();

    [GeneratedRegex(@"Dart SDK version:\s*(\S+)")]
    private static partial Regex DartVersion();

    [GeneratedRegex(@"Android emulator version\s+([^\s(]+)")]
    private static partial Regex EmulatorVersion();

    [GeneratedRegex(@"(?m)^scrcpy\s+(\S+)")]
    private static partial Regex ScrcpyVersion();

    [GeneratedRegex(@"^v?(\d+\.\d+\.\d+\S*)")]
    private static partial Regex NumericVersion();
}
