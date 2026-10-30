namespace DroidDeck.Core;

/// <summary>
/// Resolves and exposes the application's data directories.
/// </summary>
public sealed class AppPaths
{
    /// <summary>The marker file that enables portable mode next to the executable.</summary>
    public const string PortableMarkerFileName = "portable.marker";

    /// <summary>Creates paths rooted at an explicit data directory.</summary>
    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    /// <summary>Gets the root data directory.</summary>
    public string DataDirectory { get; }

    /// <summary>Gets the global settings file.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Gets the settings backup file.</summary>
    public string SettingsBackupFile => SettingsFile + ".bak";

    /// <summary>Gets the DPAPI-protected secrets file.</summary>
    public string SecretsFile => Path.Combine(DataDirectory, "secrets.json");

    /// <summary>Gets the application log directory.</summary>
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Gets the diagnostics export directory.</summary>
    public string DiagnosticsDirectory => Path.Combine(DataDirectory, "diagnostics");

    /// <summary>Gets the per-device record directory.</summary>
    public string DevicesDirectory => Path.Combine(DataDirectory, "devices");

    /// <summary>Gets the session directory.</summary>
    public string SessionsDirectory => Path.Combine(DataDirectory, "sessions");

    /// <summary>Gets the project document directory.</summary>
    public string ProjectsDirectory => Path.Combine(DataDirectory, "projects");

    /// <summary>Gets the saved log filter directory.</summary>
    public string FiltersDirectory => Path.Combine(DataDirectory, "filters");

    /// <summary>Gets the remembered wireless device directory.</summary>
    public string WirelessDirectory => Path.Combine(DataDirectory, "wireless");

    /// <summary>Gets the screenshot directory.</summary>
    public string ScreenshotsDirectory => Path.Combine(DataDirectory, "screenshots");

    /// <summary>Gets the screen recording directory.</summary>
    public string RecordingsDirectory => Path.Combine(DataDirectory, "recordings");

    /// <summary>Gets the installed plugin directory.</summary>
    public string PluginsDirectory => Path.Combine(DataDirectory, "plugins");

    /// <summary>Gets the plugin catalog directory.</summary>
    public string CatalogDirectory => Path.Combine(DataDirectory, "catalog");

    /// <summary>Gets the directory that stores cached catalogs for offline browsing.</summary>
    public string CatalogCacheDirectory => Path.Combine(CatalogDirectory, "cache");

    /// <summary>Gets the directory used to quarantine unreadable files.</summary>
    public string QuarantineDirectory => Path.Combine(DataDirectory, "quarantine");

    /// <summary>Gets the cached toolchain snapshot file.</summary>
    public string ToolchainCacheFile => Path.Combine(DataDirectory, "toolchain.json");

    /// <summary>
    /// Resolves the data directory from a command-line override, a portable marker file,
    /// or the per-user local application data folder.
    /// </summary>
    public static AppPaths Resolve(string? dataDirectoryOverride = null, string? executableDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectoryOverride))
        {
            return new AppPaths(dataDirectoryOverride);
        }

        var executableRoot = executableDirectory ?? AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(executableRoot, PortableMarkerFileName)))
        {
            return new AppPaths(Path.Combine(executableRoot, "data"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(localAppData, "DroidDeck"));
    }

    /// <summary>Creates the data directory tree.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DiagnosticsDirectory);
        Directory.CreateDirectory(DevicesDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(ProjectsDirectory);
        Directory.CreateDirectory(FiltersDirectory);
        Directory.CreateDirectory(WirelessDirectory);
        Directory.CreateDirectory(ScreenshotsDirectory);
        Directory.CreateDirectory(RecordingsDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(CatalogDirectory);
        Directory.CreateDirectory(CatalogCacheDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
    }
}
