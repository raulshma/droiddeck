using DroidDeck.Core.Configuration;
using DroidDeck.Core.Toolchain;

namespace DroidDeck.Projects;

/// <summary>Resolves tool paths from settings, the toolchain snapshot, and the environment.</summary>
public static class ToolPathLocator
{
    /// <summary>Resolves the aapt2 executable path.</summary>
    public static string? ResolveAapt2(IConfigurationService? configuration, IToolchainService? toolchain)
    {
        var configured = configuration?.Current.Settings.Tools.Aapt2Path;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var fromSnapshot = toolchain?.CachedSnapshot?.Get(ToolKind.Aapt2)?.Path;
        if (!string.IsNullOrWhiteSpace(fromSnapshot) && File.Exists(fromSnapshot))
        {
            return fromSnapshot;
        }

        var sdk = ResolveAndroidSdk(configuration, toolchain);
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return null;
        }

        var buildTools = Path.Combine(sdk, "build-tools");
        if (!Directory.Exists(buildTools))
        {
            return null;
        }

        return Directory
            .EnumerateDirectories(buildTools)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path, "aapt2.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Resolves the Android SDK root.</summary>
    public static string? ResolveAndroidSdk(IConfigurationService? configuration, IToolchainService? toolchain)
    {
        var configured = configuration?.Current.Settings.Tools.AndroidSdkPath;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        var fromSnapshot = toolchain?.CachedSnapshot?.AndroidSdkPath;
        if (!string.IsNullOrWhiteSpace(fromSnapshot) && Directory.Exists(fromSnapshot))
        {
            return fromSnapshot;
        }

        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Resolves the adb executable path.</summary>
    public static string ResolveAdb(IConfigurationService? configuration, IToolchainService? toolchain)
    {
        var configured = configuration?.Current.Settings.Tools.AdbPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromSnapshot = toolchain?.CachedSnapshot?.Get(ToolKind.Adb)?.Path;
        if (!string.IsNullOrWhiteSpace(fromSnapshot))
        {
            return fromSnapshot;
        }

        var sdk = ResolveAndroidSdk(configuration, toolchain);
        if (!string.IsNullOrWhiteSpace(sdk))
        {
            var adb = Path.Combine(sdk, "platform-tools", "adb.exe");
            if (File.Exists(adb))
            {
                return adb;
            }
        }

        return "adb";
    }
}
