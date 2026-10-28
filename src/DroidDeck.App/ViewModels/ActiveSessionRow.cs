using DroidDeck.Core.Runs;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.ViewModels;

/// <summary>Presents one in-flight run session in the home cockpit.</summary>
public sealed class ActiveSessionRow : ObservableObject
{
    private static readonly string[] PropertyNames =
    [
        nameof(Title),
        nameof(DeviceLabel),
        nameof(StateLabel),
        nameof(StateBrush),
        nameof(StepLabel),
        nameof(HasStep),
    ];

    private readonly string _openId;
    private RunSession _session;
    private string _elapsedLabel;

    /// <summary>Creates a row for a session.</summary>
    public ActiveSessionRow(RunSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _elapsedLabel = RelativeTime.Span(DateTimeOffset.UtcNow - session.StartedAt);
        _openId = AutomationId.For("HomeSession", session.Id);
    }

    /// <summary>Gets the session identifier.</summary>
    public string Id => _session.Id;

    /// <summary>Gets the project and configuration title.</summary>
    public string Title => string.IsNullOrWhiteSpace(_session.ConfigurationName)
        ? _session.ProjectName
        : $"{_session.ProjectName} / {_session.ConfigurationName}";

    /// <summary>Gets the target device.</summary>
    public string DeviceLabel => string.IsNullOrWhiteSpace(_session.DeviceName)
        ? string.IsNullOrWhiteSpace(_session.DeviceSerial) ? "No device" : _session.DeviceSerial
        : _session.DeviceName;

    /// <summary>Gets how long the session has been running.</summary>
    public string ElapsedLabel => _elapsedLabel;

    /// <summary>Gets the session state.</summary>
    public RunSessionState State => _session.State;

    /// <summary>Gets the friendly state name.</summary>
    public string StateLabel => _session.State switch
    {
        RunSessionState.Queued => "Queued",
        RunSessionState.Preparing => "Preparing",
        RunSessionState.Building => "Building",
        RunSessionState.ResolvingArtifact => "Resolving artifact",
        RunSessionState.Installing => "Installing",
        RunSessionState.Launching => "Launching",
        RunSessionState.Running => "Running",
        RunSessionState.Stopping => "Stopping",
        RunSessionState.Succeeded => "Succeeded",
        RunSessionState.Failed => "Failed",
        RunSessionState.Cancelled => "Cancelled",
        RunSessionState.PartiallyFailed => "Partially failed",
        _ => _session.State.ToString(),
    };

    /// <summary>Gets the state colour.</summary>
    public Brush StateBrush => ThemeBrushes.For(_session.State);

    /// <summary>Gets the active step name.</summary>
    public string StepLabel => _session.ActiveStep?.Name ?? _session.StatusMessage ?? string.Empty;

    /// <summary>Gets whether a step or status message is available.</summary>
    public bool HasStep => StepLabel.Length > 0;

    /// <summary>Gets the open button automation identifier.</summary>
    public string OpenId => _openId;

    /// <summary>Applies a session snapshot to this row.</summary>
    public void Update(RunSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _elapsedLabel = RelativeTime.Span(DateTimeOffset.UtcNow - session.StartedAt);
        OnPropertyChanged(nameof(ElapsedLabel));
        foreach (var property in PropertyNames)
        {
            OnPropertyChanged(property);
        }
    }

    /// <summary>Refreshes the elapsed time.</summary>
    public void Tick()
    {
        var elapsed = RelativeTime.Span(DateTimeOffset.UtcNow - _session.StartedAt);
        if (string.Equals(elapsed, _elapsedLabel, StringComparison.Ordinal))
        {
            return;
        }

        _elapsedLabel = elapsed;
        OnPropertyChanged(nameof(ElapsedLabel));
    }
}
