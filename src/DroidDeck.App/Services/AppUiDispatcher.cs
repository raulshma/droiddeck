using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Dispatching;

namespace DroidDeck.App.Services;

/// <summary>Marshals renderer work onto the thread that owns the shell.</summary>
public sealed class AppUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    /// <summary>Creates the dispatcher for the calling UI thread.</summary>
    public AppUiDispatcher()
    {
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The UI dispatcher must be created on the UI thread.");
    }

    /// <inheritdoc />
    public bool HasThreadAccess => _queue.HasThreadAccess;

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("The UI dispatcher is shutting down."));
        }

        return completion.Task;
    }
}
