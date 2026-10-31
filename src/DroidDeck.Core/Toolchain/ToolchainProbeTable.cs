using DroidDeck.Core.Configuration;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Core.Toolchain;

/// <summary>
/// Describes one toolchain probe: the executable names and version command used by the
/// shared probe path, plus the guidance shown when the tool is missing.
/// </summary>
internal sealed record ToolProbeSpec(
    ToolKind Kind,
    string DisplayName,
    Func<ToolsSettings, string?> OverridePath,
    IReadOnlyList<string> PathNames,
    IReadOnlyList<string> VersionArguments,
    Func<string, string?> VersionParser,
    string Guidance,
    TimeSpan Timeout,
    Func<ToolProbeContext, IReadOnlyList<string>>? ExtraCandidates = null,
    Func<ToolchainDoctor, ToolProbeContext, CancellationToken, Task<ToolInfo>>? CustomProbe = null);

/// <summary>The inputs shared by every probe in one toolchain check.</summary>
internal sealed record ToolProbeContext(
    ToolsSettings Tools,
    ToolInfo? AndroidSdk,
    ToolchainEnvironment Environment,
    Task<ToolInfo>? DotNetTask = null)
{
    /// <summary>Gets the resolved Android SDK root, when the SDK is available.</summary>
    public string? AndroidSdkPath => AndroidSdk is { IsAvailable: true } sdk ? sdk.Path : null;
}

/// <summary>
/// The single source of truth for how each <see cref="ToolKind"/> is discovered.
/// Probes with uniform executable discovery are rows here; only discovery that cannot
/// be expressed by the shared probe path supplies a custom delegate.
/// </summary>
internal static class ToolchainProbeTable
{
    internal static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan FlutterProbeTimeout = TimeSpan.FromSeconds(30);

    internal const string AndroidSdkGuidance =
        "Install Android Studio or the Android SDK command-line tools, then set the SDK path in Settings > Tools.";

    internal const string UnityGuidance =
        "Install Unity Hub and an editor with Android Build Support, or set the Unity path in Settings > Tools.";

    internal const string MauiWorkloadGuidance =
        "Install the .NET MAUI Android workload by running \"dotnet workload install maui-android\", then restart DroidDeck.";

    private const string AdbGuidance =
        "Install Android SDK Platform-Tools through Android Studio's SDK Manager, or set the adb path in Settings > Tools.";

    private const string Aapt2Guidance =
        "Install Android SDK Build-Tools through Android Studio's SDK Manager, or set the aapt2 path in Settings > Tools.";

    private const string EmulatorGuidance =
        "Install the Android Emulator package through Android Studio's SDK Manager, or set the emulator path in Settings > Tools.";

    private const string JavaGuidance =
        "Install a JDK 17 or newer and set JAVA_HOME, or set the JDK path in Settings > Tools.";

    private const string GradleGuidance =
        "Use a project Gradle wrapper or install Gradle and add it to PATH, then set the Gradle path in Settings > Tools if needed.";

    private const string FlutterGuidance =
        "Install Flutter and add its bin folder to PATH, or set the Flutter path in Settings > Tools.";

    private const string DartGuidance =
        "Install the Flutter SDK (which bundles Dart) or the Dart SDK and add it to PATH, or set the Dart path in Settings > Tools.";

    private const string NodeGuidance =
        "Install Node.js and add it to PATH, or set the Node.js path in Settings > Tools.";

    private const string ScrcpyGuidance =
        "Install scrcpy and add it to PATH to use screen mirroring, or set the scrcpy path in Settings > Tools.";

    private const string DotNetGuidance =
        "Install the .NET SDK and add it to PATH, or set the dotnet path in Settings > Tools.";

    private static readonly string[] DartExecutableNames = ["dart.exe", "dart.cmd", "dart.bat"];

    /// <summary>Gets the specification for every <see cref="ToolKind"/>, in enum order.</summary>
    public static IReadOnlyList<ToolProbeSpec> All { get; } =
    [
        Custom(
            ToolKind.AndroidSdk,
            "Android SDK",
            static tools => tools.AndroidSdkPath,
            AndroidSdkGuidance,
            static (_, context, _) => Task.FromResult(
                context.AndroidSdk
                ?? throw new InvalidOperationException("The Android SDK probe requires a resolved SDK context."))),
        new(
            ToolKind.Adb,
            "adb",
            static tools => tools.AdbPath,
            ["adb.exe", "adb.cmd", "adb.bat"],
            ["version"],
            ToolVersionParser.Adb,
            AdbGuidance,
            DefaultProbeTimeout,
            static context => SdkCandidates(context, "platform-tools", "adb")),
        new(
            ToolKind.Aapt2,
            "aapt2",
            static tools => tools.Aapt2Path,
            ["aapt2.exe", "aapt2.cmd", "aapt2.bat"],
            ["version"],
            ToolVersionParser.LastTokenOfFirstLine,
            Aapt2Guidance,
            DefaultProbeTimeout,
            Aapt2Candidates),
        new(
            ToolKind.Emulator,
            "Android emulator",
            static tools => tools.EmulatorPath,
            ["emulator.exe", "emulator.cmd", "emulator.bat"],
            ["-version"],
            ToolVersionParser.Emulator,
            EmulatorGuidance,
            DefaultProbeTimeout,
            static context => SdkCandidates(context, "emulator", "emulator")),
        new(
            ToolKind.Java,
            "Java",
            static tools => tools.JavaHome,
            ["java.exe", "java.cmd", "java.bat"],
            ["-version"],
            ToolVersionParser.Java,
            JavaGuidance,
            DefaultProbeTimeout,
            JavaCandidates,
            static (doctor, context, cancellationToken) => doctor.ProbeJavaAsync(context, cancellationToken)),
        new(
            ToolKind.Gradle,
            "Gradle",
            static tools => tools.GradlePath,
            ["gradle.bat", "gradle.exe", "gradle"],
            ["--version"],
            ToolVersionParser.Gradle,
            GradleGuidance,
            DefaultProbeTimeout),
        new(
            ToolKind.Flutter,
            "Flutter",
            static tools => tools.FlutterPath,
            ["flutter.bat", "flutter.exe", "flutter"],
            ["--version"],
            ToolVersionParser.Flutter,
            FlutterGuidance,
            FlutterProbeTimeout,
            FlutterCandidates),
        new(
            ToolKind.Dart,
            "Dart",
            static tools => tools.DartPath,
            DartExecutableNames,
            ["--version"],
            ToolVersionParser.Dart,
            DartGuidance,
            DefaultProbeTimeout,
            DartCandidates),
        new(
            ToolKind.Node,
            "Node.js",
            static tools => tools.NodePath,
            ["node.exe", "node.cmd", "node.bat"],
            ["--version"],
            ToolVersionParser.Numeric,
            NodeGuidance,
            DefaultProbeTimeout),
        new(
            ToolKind.Npm,
            "npm",
            static _ => null,
            ["npm.cmd", "npm.exe"],
            ["--version"],
            ToolVersionParser.Numeric,
            NodeGuidance,
            DefaultProbeTimeout,
            static context => NodeSiblingCandidates(context, "npm.cmd")),
        new(
            ToolKind.Npx,
            "npx",
            static _ => null,
            ["npx.cmd", "npx.exe"],
            ["--version"],
            ToolVersionParser.Numeric,
            NodeGuidance,
            DefaultProbeTimeout,
            static context => NodeSiblingCandidates(context, "npx.cmd")),
        new(
            ToolKind.Scrcpy,
            "scrcpy",
            static tools => tools.ScrcpyPath,
            ["scrcpy.exe", "scrcpy.cmd", "scrcpy.bat"],
            ["--version"],
            ToolVersionParser.Scrcpy,
            ScrcpyGuidance,
            DefaultProbeTimeout,
            ScrcpyCandidates),
        Custom(
            ToolKind.Unity,
            "Unity",
            static tools => tools.UnityPath,
            UnityGuidance,
            static (doctor, context, _) => Task.FromResult(doctor.ProbeUnity(context.Tools.UnityPath))),
        new(
            ToolKind.DotNet,
            ".NET SDK",
            static tools => tools.DotNetPath,
            ["dotnet.exe", "dotnet.cmd", "dotnet.bat"],
            ["--version"],
            ToolVersionParser.Numeric,
            DotNetGuidance,
            DefaultProbeTimeout,
            DotNetCandidates),
        Custom(
            ToolKind.MauiAndroidWorkload,
            ".NET MAUI Android workload",
            static _ => null,
            MauiWorkloadGuidance,
            static (doctor, context, cancellationToken) => doctor.ProbeMauiAndroidWorkloadAsync(
                context.DotNetTask
                ?? throw new InvalidOperationException("The MAUI workload probe requires the .NET SDK probe."),
                cancellationToken)),
    ];

    private static readonly Dictionary<ToolKind, ToolProbeSpec> ByKind =
        All.ToDictionary(spec => spec.Kind);

    /// <summary>Gets the specification for a tool kind.</summary>
    public static ToolProbeSpec Get(ToolKind kind) => ByKind[kind];

    private static ToolProbeSpec Custom(
        ToolKind kind,
        string displayName,
        Func<ToolsSettings, string?> overridePath,
        string guidance,
        Func<ToolchainDoctor, ToolProbeContext, CancellationToken, Task<ToolInfo>> customProbe) => new(
            kind,
            displayName,
            overridePath,
            [],
            [],
            static _ => null,
            guidance,
            DefaultProbeTimeout,
            CustomProbe: customProbe);

    private static List<string> SdkCandidates(ToolProbeContext context, string folder, string fileBase)
    {
        var extras = new List<string>();
        AddUnderSdk(extras, context.AndroidSdkPath, folder, fileBase);
        return extras;
    }

    private static List<string> Aapt2Candidates(ToolProbeContext context)
    {
        var extras = new List<string>();
        if (context.AndroidSdkPath is not null)
        {
            var latest = FindLatestBuildTool(context.AndroidSdkPath, "aapt2");
            if (latest is not null)
            {
                extras.Add(latest);
            }
        }

        return extras;
    }

    private static List<string> JavaCandidates(ToolProbeContext context)
    {
        var extras = new List<string>();
        var environment = context.Environment;
        AddJavaHome(extras, environment.GetVariable("JAVA_HOME"));
        if (!string.IsNullOrWhiteSpace(environment.ProgramFiles))
        {
            AddJavaHome(extras, Path.Combine(environment.ProgramFiles, "Android", "Android Studio", "jbr"));
            AddJavaHome(extras, Path.Combine(environment.ProgramFiles, "Android", "Android Studio Preview", "jbr"));
        }

        if (!string.IsNullOrWhiteSpace(environment.LocalApplicationData))
        {
            AddJavaHome(extras, Path.Combine(environment.LocalApplicationData, "Programs", "Android Studio", "jbr"));
        }

        return extras;
    }

    private static List<string> FlutterCandidates(ToolProbeContext context) =>
        FlutterBinDirectories(context.Environment)
            .Select(directory => Path.Combine(directory, "flutter.bat"))
            .ToList();

    private static List<string> DartCandidates(ToolProbeContext context)
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Tools.FlutterPath))
        {
            AddDartCandidates(extras, Path.GetDirectoryName(context.Tools.FlutterPath));
        }

        foreach (var directory in FlutterBinDirectories(context.Environment))
        {
            AddDartCandidates(extras, directory);
        }

        return extras;
    }

    private static List<string> NodeSiblingCandidates(ToolProbeContext context, string fileName)
    {
        var extras = new List<string>();
        AddIfPresent(extras, ResolveNodeSibling(context.Tools.NodePath, fileName));
        return extras;
    }

    private static List<string> ScrcpyCandidates(ToolProbeContext context)
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Environment.LocalApplicationData))
        {
            extras.Add(Path.Combine(
                context.Environment.LocalApplicationData,
                "Microsoft",
                "WinGet",
                "Links",
                "scrcpy.exe"));
        }

        return extras;
    }

    private static List<string> DotNetCandidates(ToolProbeContext context)
    {
        var extras = new List<string>();
        var dotnetRoot = context.Environment.GetVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            extras.Add(Path.Combine(dotnetRoot, "dotnet.exe"));
        }

        if (!string.IsNullOrWhiteSpace(context.Environment.ProgramFiles))
        {
            extras.Add(Path.Combine(context.Environment.ProgramFiles, "dotnet", "dotnet.exe"));
        }

        return extras;
    }

    private static IEnumerable<string> FlutterBinDirectories(ToolchainEnvironment environment)
    {
        var flutterRoot = environment.GetVariable("FLUTTER_ROOT");
        if (!string.IsNullOrWhiteSpace(flutterRoot))
        {
            yield return Path.Combine(flutterRoot, "bin");
        }

        if (!string.IsNullOrWhiteSpace(environment.LocalApplicationData))
        {
            yield return Path.Combine(environment.LocalApplicationData, "flutter", "bin");
        }

        if (!string.IsNullOrWhiteSpace(environment.UserProfile))
        {
            yield return Path.Combine(environment.UserProfile, "flutter", "bin");
        }
    }

    private static void AddDartCandidates(List<string> target, string? binDirectory)
    {
        if (string.IsNullOrWhiteSpace(binDirectory))
        {
            return;
        }

        foreach (var name in DartExecutableNames)
        {
            target.Add(Path.Combine(binDirectory, name));
        }

        target.Add(Path.Combine(binDirectory, "cache", "dart-sdk", "bin", "dart.exe"));
    }

    private static void AddUnderSdk(List<string> target, string? sdkPath, string folder, string fileBase)
    {
        if (sdkPath is null)
        {
            return;
        }

        var directory = Path.Combine(sdkPath, folder);
        foreach (var extension in new[] { "exe", "cmd", "bat" })
        {
            target.Add(Path.Combine(directory, fileBase + "." + extension));
        }
    }

    private static void AddJavaHome(List<string> target, string? javaHome)
    {
        if (string.IsNullOrWhiteSpace(javaHome))
        {
            return;
        }

        target.Add(Path.Combine(javaHome, "bin", "java.exe"));
    }

    private static void AddIfPresent(List<string> target, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            target.Add(path);
        }
    }

    private static string? ResolveNodeSibling(string? nodePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(nodePath);
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, fileName);
    }

    private static string? FindLatestBuildTool(string sdkPath, string fileBase)
    {
        var buildTools = Path.Combine(sdkPath, "build-tools");
        if (!Directory.Exists(buildTools))
        {
            return null;
        }

        try
        {
            var candidates = new List<(string File, SemanticVersion? Version)>();
            foreach (var directory in Directory.EnumerateDirectories(buildTools))
            {
                var file = FindInDirectory(directory, fileBase);
                if (file is not null)
                {
                    var name = Path.GetFileName(directory);
                    candidates.Add((file, SemanticVersion.TryParse(name, out var version) ? version : null));
                }
            }

            return candidates
                .OrderByDescending(candidate => candidate.Version, NullSafeVersionComparer.Instance)
                .ThenByDescending(candidate => candidate.File, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.File)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindInDirectory(string directory, string fileBase)
    {
        foreach (var extension in new[] { "exe", "cmd", "bat" })
        {
            var candidate = Path.Combine(directory, fileBase + "." + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Orders nullable semantic versions with missing versions first.</summary>
internal sealed class NullSafeVersionComparer : IComparer<SemanticVersion?>
{
    public static readonly NullSafeVersionComparer Instance = new();

    public int Compare(SemanticVersion? x, SemanticVersion? y) => x is null
        ? y is null ? 0 : -1
        : y is null ? 1 : x.CompareTo(y);
}
