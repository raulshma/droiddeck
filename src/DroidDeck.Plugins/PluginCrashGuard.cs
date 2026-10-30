using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>Describes a plugin that crossed the crash threshold.</summary>
public sealed class PluginCrashEventArgs : EventArgs
{
    /// <summary>Creates crash event data.</summary>
    public PluginCrashEventArgs(string pluginId, int failureCount, string? lastError, bool timedOut)
    {
        PluginId = pluginId;
        FailureCount = failureCount;
        LastError = lastError;
        TimedOut = timedOut;
    }

    /// <summary>Gets the plugin identifier.</summary>
    public string PluginId { get; }

    /// <summary>Gets the run of consecutive failures that tripped the guard.</summary>
    public int FailureCount { get; }

    /// <summary>Gets the last failure message.</summary>
    public string? LastError { get; }

    /// <summary>Gets whether the last failure was a timeout.</summary>
    public bool TimedOut { get; }
}

/// <summary>
/// Watches plugin health and reports a plugin once it fails
/// <see cref="DefaultThreshold"/> times in a row. The host disables the plugin
/// in response, so a repeatedly crashing plugin stops running until the user
/// enables it again. A successful invocation resets the run.
/// </summary>
public sealed class PluginCrashGuard : IDisposable
{
    /// <summary>The number of consecutive failures that disables a plugin.</summary>
    public const int DefaultThreshold = 3;

    private readonly PluginHealthTracker _health;
    private readonly ILogger<PluginCrashGuard> _logger;
    private readonly HashSet<string> _tripped = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates a guard over the shared health tracker.</summary>
    public PluginCrashGuard(
        PluginHealthTracker health,
        int threshold = DefaultThreshold,
        ILogger<PluginCrashGuard>? logger = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        if (threshold < DefaultThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(threshold),
                $"A plugin is only disabled after at least {DefaultThreshold} consecutive failures.");
        }

        Threshold = threshold;
        _logger = logger ?? NullLogger<PluginCrashGuard>.Instance;
        _health.HealthChanged += OnHealthChanged;
    }

    /// <summary>Gets the effective failure threshold.</summary>
    public int Threshold { get; }

    /// <summary>Raised once for each run of failures that reaches the threshold.</summary>
    public event EventHandler<PluginCrashEventArgs>? Tripped;

    /// <summary>Gets whether a plugin is currently over the threshold.</summary>
    public bool IsTripped(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _tripped.Contains(pluginId);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _health.HealthChanged -= OnHealthChanged;
    }

    private void OnHealthChanged(object? sender, EventArgs e)
    {
        foreach (var snapshot in _health.Snapshot())
        {
            if (snapshot.ConsecutiveFailures == 0)
            {
                lock (_sync)
                {
                    _tripped.Remove(snapshot.PluginId);
                }

                continue;
            }

            if (snapshot.ConsecutiveFailures < Threshold)
            {
                continue;
            }

            lock (_sync)
            {
                if (!_tripped.Add(snapshot.PluginId))
                {
                    continue;
                }
            }

            try
            {
                Tripped?.Invoke(
                    this,
                    new PluginCrashEventArgs(
                        snapshot.PluginId,
                        snapshot.ConsecutiveFailures,
                        snapshot.LastError,
                        snapshot.LastFailureTimedOut));
            }
            catch (Exception exception)
            {
                // A failing crash handler must never turn into another plugin failure.
                _logger.LogWarning(
                    exception,
                    "A crash handler for plugin {PluginId} failed.",
                    snapshot.PluginId);
            }
        }
    }
}
