namespace DroidDeck.Plugins;

/// <summary>Health information for one plugin.</summary>
public sealed record PluginHealthSnapshot
{
    /// <summary>Gets the plugin identifier.</summary>
    public required string PluginId { get; init; }

    /// <summary>Gets the number of successful invocations.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Gets the number of failed invocations.</summary>
    public int FailureCount { get; init; }

    /// <summary>Gets the current run of consecutive failures.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Gets the last failure message.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets when the last failure occurred.</summary>
    public DateTimeOffset? LastFailureAt { get; init; }

    /// <summary>Gets whether the last failure was a timeout.</summary>
    public bool LastFailureTimedOut { get; init; }

    /// <summary>Gets whether the plugin is currently healthy.</summary>
    public bool IsHealthy => ConsecutiveFailures == 0;

    /// <summary>Gets the health label shown by the UI.</summary>
    public string StatusLabel => IsHealthy ? "Healthy" : "Unhealthy";
}

/// <summary>
/// Tracks plugin invocation outcomes. A failure marks the plugin unhealthy
/// until a later invocation succeeds.
/// </summary>
public sealed class PluginHealthTracker
{
    private readonly Dictionary<string, MutableHealth> _health = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    /// <summary>Creates a tracker.</summary>
    public PluginHealthTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after a plugin's health changes.</summary>
    public event EventHandler? HealthChanged;

    /// <summary>Records a successful invocation.</summary>
    public void RecordSuccess(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
        {
            var entry = GetOrCreate(pluginId);
            entry.SuccessCount++;
            entry.ConsecutiveFailures = 0;
            entry.LastError = null;
            entry.LastFailureAt = null;
            entry.LastFailureTimedOut = false;
        }

        RaiseChanged();
    }

    /// <summary>Records a failed invocation and marks the plugin unhealthy.</summary>
    public void RecordFailure(string pluginId, string message, bool timedOut = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var entry = GetOrCreate(pluginId);
            entry.FailureCount++;
            entry.ConsecutiveFailures++;
            entry.LastError = message;
            entry.LastFailureAt = _timeProvider.GetUtcNow();
            entry.LastFailureTimedOut = timedOut;
        }

        RaiseChanged();
    }

    /// <summary>Gets a snapshot for one plugin, or a healthy default when it has no history.</summary>
    public PluginHealthSnapshot Get(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
        {
            return _health.TryGetValue(pluginId, out var entry)
                ? entry.ToSnapshot(pluginId)
                : new PluginHealthSnapshot { PluginId = pluginId };
        }
    }

    /// <summary>Gets every tracked plugin, ordered by identifier.</summary>
    public IReadOnlyList<PluginHealthSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return _health
                .Select(pair => pair.Value.ToSnapshot(pair.Key))
                .OrderBy(static snapshot => snapshot.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private MutableHealth GetOrCreate(string pluginId)
    {
        if (!_health.TryGetValue(pluginId, out var entry))
        {
            entry = new MutableHealth();
            _health[pluginId] = entry;
        }

        return entry;
    }

    private void RaiseChanged() => HealthChanged?.Invoke(this, EventArgs.Empty);

    private sealed class MutableHealth
    {
        public int SuccessCount { get; set; }

        public int FailureCount { get; set; }

        public int ConsecutiveFailures { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset? LastFailureAt { get; set; }

        public bool LastFailureTimedOut { get; set; }

        public PluginHealthSnapshot ToSnapshot(string pluginId) => new()
        {
            PluginId = pluginId,
            SuccessCount = SuccessCount,
            FailureCount = FailureCount,
            ConsecutiveFailures = ConsecutiveFailures,
            LastError = LastError,
            LastFailureAt = LastFailureAt,
            LastFailureTimedOut = LastFailureTimedOut,
        };
    }
}
