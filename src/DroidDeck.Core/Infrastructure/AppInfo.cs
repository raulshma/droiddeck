using System.Reflection;
using System.Runtime.InteropServices;

namespace DroidDeck.Core.Infrastructure;

/// <summary>
/// Version and runtime information used by diagnostics and the About surface.
/// </summary>
public static class AppInfo
{
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
}
