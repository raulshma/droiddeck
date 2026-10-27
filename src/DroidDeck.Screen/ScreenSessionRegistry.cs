using DroidDeck.Core.Screen;

namespace DroidDeck.Screen;

/// <summary>
/// Tracks active mirror sessions. Screen hosting is implemented in the screen milestone;
/// diagnostics can already report active mirrors through this service.
/// </summary>
public sealed class ScreenSessionRegistry : IScreenService
{
    private readonly List<ScreenSessionInfo> _sessions = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public IReadOnlyList<ScreenSessionInfo> ActiveSessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.ToList();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? SessionsChanged;

    /// <summary>Records a started mirror session.</summary>
    public void Register(ScreenSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            if (_sessions.Any(existing => string.Equals(existing.Serial, session.Serial, StringComparison.Ordinal)))
            {
                return;
            }

            _sessions.Add(session);
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a mirror session.</summary>
    public bool Unregister(string serial)
    {
        bool removed;
        lock (_lock)
        {
            removed = _sessions.RemoveAll(existing =>
                string.Equals(existing.Serial, serial, StringComparison.Ordinal)) > 0;
        }

        if (removed)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }
}
