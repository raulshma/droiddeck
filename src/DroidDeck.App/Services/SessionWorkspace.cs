using DroidDeck.Core.Runs;

namespace DroidDeck.App.Services;

/// <summary>
/// Tracks which project, device, and run session the workspace is showing so the
/// projects, devices, history, notification, and session pages can navigate to the
/// same selection.
/// </summary>
public sealed class SessionWorkspace
{
    private readonly object _sync = new();
    private string? _projectId;
    private string? _sessionId;
    private string? _deviceSerial;
    private RunSession? _readOnlySession;

    /// <summary>Raised when the selected project or session changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the selected project identifier.</summary>
    public string? ProjectId
    {
        get
        {
            lock (_sync)
            {
                return _projectId;
            }
        }
    }

    /// <summary>Gets the selected session identifier.</summary>
    public string? SessionId
    {
        get
        {
            lock (_sync)
            {
                return _sessionId;
            }
        }
    }

    /// <summary>Gets the read-only session snapshot when history is open.</summary>
    public RunSession? ReadOnlySession
    {
        get
        {
            lock (_sync)
            {
                return _readOnlySession;
            }
        }
    }

    /// <summary>Gets the device serial another page asked the devices page to show.</summary>
    public string? DeviceSerial
    {
        get
        {
            lock (_sync)
            {
                return _deviceSerial;
            }
        }
    }

    /// <summary>Selects a project without changing the session.</summary>
    public void OpenProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        lock (_sync)
        {
            _projectId = projectId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a device without changing the project or session.</summary>
    public void OpenDevice(string deviceSerial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        lock (_sync)
        {
            _deviceSerial = deviceSerial;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a live session.</summary>
    public void OpenSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_sync)
        {
            _sessionId = sessionId;
            _readOnlySession = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a session loaded from history.</summary>
    public void OpenHistorySession(RunSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            _sessionId = session.Id;
            _readOnlySession = session;
            _projectId = string.IsNullOrWhiteSpace(session.ProjectId) ? _projectId : session.ProjectId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the selected session.</summary>
    public void ClearSession()
    {
        lock (_sync)
        {
            _sessionId = null;
            _readOnlySession = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
