using DroidDeck.Core;
using DroidDeck.Core.Configuration;

namespace DroidDeck.App;

/// <summary>Command-line options understood by the shell.</summary>
public sealed class AppOptions
{
    /// <summary>Gets the data directory override.</summary>
    public string? DataDirectory { get; private set; }

    /// <summary>Gets the session-layer overrides from --set, --profile, and DROIDDECK__* variables.</summary>
    public CliOverrideSet Overrides { get; private set; } = CliOverrideSet.Empty;

    /// <summary>Gets whether the diagnostics page should open on launch.</summary>
    public bool OpenDiagnostics { get; private set; }

    /// <summary>Gets whether test mode is active.</summary>
    public bool TestMode { get; private set; }

    /// <summary>Gets whether plugins are disabled for this launch.</summary>
    public bool NoPlugins { get; private set; }

    /// <summary>Gets whether safe mode is requested.</summary>
    public bool SafeMode { get; private set; }

    /// <summary>Gets whether the version should be printed and the app should exit.</summary>
    public bool ShowVersion { get; private set; }

    /// <summary>Gets whether help should be printed and the app should exit.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>Gets whether this is a test-mode launch with a managed temporary data directory.</summary>
    public bool UsesTemporaryDataDirectory { get; private set; }

    /// <summary>Gets the raw arguments forwarded by a second launch.</summary>
    public IReadOnlyList<string> RawArguments { get; private set; } = [];

    /// <summary>Parses command-line arguments.</summary>
    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var options = new AppOptions { RawArguments = [.. args] };
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument.ToLowerInvariant())
            {
                case "--data-dir" when index + 1 < args.Count:
                    options.DataDirectory = args[++index];
                    break;
                case "--diagnostics":
                    options.OpenDiagnostics = true;
                    break;
                case "--test-mode":
                    options.TestMode = true;
                    break;
                case "--safe-mode":
                    options.SafeMode = true;
                    break;
                case "--no-plugins":
                    options.NoPlugins = true;
                    break;
                case "--version":
                    options.ShowVersion = true;
                    break;
                case "--help" or "-h" or "/?":
                    options.ShowHelp = true;
                    break;
            }
        }

        if (options.TestMode && string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            options.DataDirectory = Path.Combine(
                Path.GetTempPath(),
                "DroidDeck",
                "test-" + Guid.NewGuid().ToString("N"));
            options.UsesTemporaryDataDirectory = true;
        }

        var overrides = CliOverrides.ParseArguments(args);
        if (overrides.ProfileId is { } profileId)
        {
            // The profile rides the session layer so a CLI profile request
            // stays ephemeral instead of rewriting the saved active profile.
            overrides = new CliOverrideSet(
                [.. overrides.Settings, new KeyValuePair<string, string>("profile.activeId", profileId)],
                ProfileId: null);
        }

        options.Overrides = overrides;
        return options;
    }

    /// <summary>Resolves data paths for this launch.</summary>
    public AppPaths ResolvePaths() => AppPaths.Resolve(DataDirectory);

    /// <summary>Gets the help text.</summary>
    public static string GetHelpText() =>
        """
        DroidDeck.exe [options]

          --data-dir <path>   Use a custom data directory (portable mode)
          --test-mode         Use a temporary data directory
          --safe-mode         Disable plugins and experimental features
          --no-plugins        Disable plugins without changing saved settings
          --diagnostics       Open the diagnostics page
          --set <path>=<value>  Override one setting for this session only
          --profile <id>      Activate a profile for this session only
          --version           Print the version
          --help              Print this help

        DROIDDECK__SECTION__KEY environment variables also override settings
        for this session. A second launch focuses the running instance and
        forwards these options.
        """;
}
