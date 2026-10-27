namespace DroidDeck.Core.Screen;

/// <summary>Information about an active screen session.</summary>
/// <param name="Serial">The mirrored device serial.</param>
/// <param name="StartedAt">When the session started.</param>
public sealed record ScreenSessionInfo(string Serial, DateTimeOffset StartedAt);

/// <summary>
/// Tracks screen mirroring sessions. The scrcpy host arrives with the screen milestone.
/// </summary>
public interface IScreenService
{
    /// <summary>Gets active mirror sessions.</summary>
    IReadOnlyList<ScreenSessionInfo> ActiveSessions { get; }

    /// <summary>Raised when the active session set changes.</summary>
    event EventHandler? SessionsChanged;
}
