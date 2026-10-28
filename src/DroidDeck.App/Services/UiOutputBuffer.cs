using System.Text;
using Microsoft.UI.Dispatching;

namespace DroidDeck.App.Services;

/// <summary>
/// Coalesces lines produced off the UI thread into one dispatcher callback so
/// chatty commands do not enqueue thousands of individual UI updates.
/// </summary>
internal sealed class UiOutputBuffer
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _append;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private int _scheduled;

    /// <summary>Creates a buffer that appends batches on the dispatcher thread.</summary>
    public UiOutputBuffer(DispatcherQueue dispatcher, Action<string> append)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _append = append ?? throw new ArgumentNullException(nameof(append));
    }

    /// <summary>Queues a line for the next UI batch.</summary>
    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Append(text).AppendLine();
        }

        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
        {
            return;
        }

        if (!_dispatcher.TryEnqueue(Flush))
        {
            Volatile.Write(ref _scheduled, 0);
        }
    }

    /// <summary>Drops buffered lines that have not reached the UI yet.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    private void Flush()
    {
        string text;
        lock (_gate)
        {
            text = _pending.ToString();
            _pending.Clear();
            Volatile.Write(ref _scheduled, 0);
        }

        if (text.Length > 0)
        {
            _append(text);
        }
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports synchronously on the producing thread.</summary>
internal sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => action(value);
}
