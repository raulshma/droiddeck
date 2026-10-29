namespace DroidDeck.Core.Configuration;

/// <summary>
/// Signals consumers when the configuration service publishes a new snapshot.
/// Once <see cref="HasChanged"/> flips, obtain a fresh token to observe the next change.
/// </summary>
public sealed class ConfigChangeToken : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Gets whether a new snapshot was published since the token was created.</summary>
    public bool HasChanged => _cts.IsCancellationRequested;

    /// <summary>Registers a callback that is invoked when a new snapshot is published.</summary>
    public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _cts.Token.Register(callback, state);
    }

    /// <summary>Releases the token; observers that never dispose keep working.</summary>
    public void Dispose() => _cts.Dispose();

    internal void NotifyChanged() => _cts.Cancel();
}
