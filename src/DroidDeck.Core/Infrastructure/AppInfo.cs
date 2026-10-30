using System.Reflection;
using System.Runtime.InteropServices;

namespace DroidDeck.Core.Infrastructure;

/// <summary>
/// Version and runtime information used by diagnostics and the About surface.
/// </summary>
public static class AppInfo
{
    /// <summary>The project repository used for documentation and release links.</summary>
    public const string RepositoryUrl = "https://github.com/raulshma/droiddeck";

    /// <summary>The GitHub API endpoint that lists the latest published release.</summary>
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/raulshma/droiddeck/releases/latest";

    /// <summary>Gets the application version from the entry assembly.</summary>
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus >= 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>Gets the .NET runtime version.</summary>
    public static string RuntimeVersion => RuntimeInformation.FrameworkDescription;

    /// <summary>Gets the operating system description.</summary>
    public static string OperatingSystem => RuntimeInformation.OSDescription;

    /// <summary>Gets the process architecture.</summary>
    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString();

    /// <summary>Gets the Windows App SDK version stamped into the entry assembly at build time.</summary>
    public static string WindowsAppSdkVersion => ReadEntryMetadata("WindowsAppSdkVersion") ?? "unknown";

    private static string? ReadEntryMetadata(string key)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.Ordinal))?.Value;
    }
}
