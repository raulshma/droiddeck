namespace DroidDeck.Core.Toolchain;

/// <summary>
/// The machine-specific inputs used by the toolchain doctor. Tests supply a
/// deterministic instance instead of reading the current process environment.
/// </summary>
public sealed record ToolchainEnvironment
{
    /// <summary>Gets the directories listed on <c>PATH</c>.</summary>
    public required IReadOnlyList<string> PathDirectories { get; init; }

    /// <summary>Gets a delegate that reads an environment variable.</summary>
    public required Func<string, string?> GetVariable { get; init; }

    /// <summary>Gets the per-user local application data directory.</summary>
    public required string LocalApplicationData { get; init; }

    /// <summary>Gets the program files directory.</summary>
    public required string ProgramFiles { get; init; }

    /// <summary>Gets the user profile directory.</summary>
    public required string UserProfile { get; init; }

    /// <summary>Reads the current process environment.</summary>
    public static ToolchainEnvironment CreateSystem()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new ToolchainEnvironment
        {
            PathDirectories = path.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            GetVariable = Environment.GetEnvironmentVariable,
            LocalApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
    }
}
