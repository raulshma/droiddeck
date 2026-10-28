using DroidDeck.Core.Runs;

namespace DroidDeck.Core.Notifications;

/// <summary>The event that produced a notification.</summary>
public enum NotificationEvent
{
    /// <summary>A run completed successfully.</summary>
    RunSucceeded,

    /// <summary>A run failed.</summary>
    RunFailed,

    /// <summary>A run was cancelled.</summary>
    RunCancelled,

    /// <summary>A crash or ANR was detected.</summary>
    Crash,

    /// <summary>An install failed.</summary>
    InstallFailed,
}

/// <summary>A notification shown in the shell.</summary>
public sealed class AppNotification
{
    /// <summary>Gets or sets the event.</summary>
    public NotificationEvent Event { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the related session identifier.</summary>
    public string? SessionId { get; set; }

    /// <summary>Gets or sets the navigation target.</summary>
    public string? NavigationTarget { get; set; }

    /// <summary>Gets or sets when the notification was raised.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Describes a notification preference matrix.</summary>
public sealed class NotificationPreferences
{
    /// <summary>Gets or sets whether notifications are enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets whether successful runs notify.</summary>
    public bool OnRunSucceeded { get; set; } = true;

    /// <summary>Gets or sets whether failed runs notify.</summary>
    public bool OnRunFailed { get; set; } = true;

    /// <summary>Gets or sets whether cancelled runs notify.</summary>
    public bool OnRunCancelled { get; set; }

    /// <summary>Gets or sets whether crashes notify.</summary>
    public bool OnCrash { get; set; } = true;

    /// <summary>Gets or sets whether install failures notify.</summary>
    public bool OnInstallFailed { get; set; } = true;

    /// <summary>Gets whether an event is enabled.</summary>
    public bool IsEnabled(NotificationEvent notificationEvent) => Enabled && notificationEvent switch
    {
        NotificationEvent.RunSucceeded => OnRunSucceeded,
        NotificationEvent.RunFailed => OnRunFailed,
        NotificationEvent.RunCancelled => OnRunCancelled,
        NotificationEvent.Crash => OnCrash,
        NotificationEvent.InstallFailed => OnInstallFailed,
        _ => false,
    };
}

/// <summary>Raises in-app notifications.</summary>
public interface INotificationService
{
    /// <summary>Gets or sets the current preferences.</summary>
    NotificationPreferences Preferences { get; set; }

    /// <summary>Raises a notification when its event is enabled.</summary>
    void Notify(AppNotification notification);

    /// <summary>Raised for every enabled notification.</summary>
    event EventHandler<AppNotification>? NotificationRaised;
}

/// <summary>A notification service that filters by the configured event matrix.</summary>
public sealed class NotificationService : INotificationService
{
    private NotificationPreferences _preferences = new();

    /// <inheritdoc />
    public NotificationPreferences Preferences
    {
        get => _preferences;
        set => _preferences = value ?? new NotificationPreferences();
    }

    /// <inheritdoc />
    public event EventHandler<AppNotification>? NotificationRaised;

    /// <inheritdoc />
    public void Notify(AppNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (!_preferences.IsEnabled(notification.Event))
        {
            return;
        }

        NotificationRaised?.Invoke(this, notification);
    }

    /// <summary>Builds a notification for a finished session.</summary>
    public static AppNotification ForSession(RunSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var notificationEvent = session.State switch
        {
            RunSessionState.Succeeded => NotificationEvent.RunSucceeded,
            RunSessionState.Cancelled => NotificationEvent.RunCancelled,
            _ => NotificationEvent.RunFailed,
        };
        return new AppNotification
        {
            Event = notificationEvent,
            Title = notificationEvent switch
            {
                NotificationEvent.RunSucceeded => "Run succeeded",
                NotificationEvent.RunCancelled => "Run cancelled",
                _ => "Run failed",
            },
            Message = $"{session.ProjectName} / {session.ConfigurationName} on {session.DeviceName ?? session.DeviceSerial}" +
                      (string.IsNullOrEmpty(session.Error) ? string.Empty : $": {session.Error}"),
            SessionId = session.Id,
            NavigationTarget = "session",
        };
    }
}
