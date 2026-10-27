using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// A thread-safe, capacity-bounded in-memory log used by the activity view and
/// diagnostics reports. Old entries are dropped once the capacity is reached.
/// </summary>
public sealed class BoundedLogBuffer
{
    private readonly object _sync = new();
    private readonly Queue<LogEntry> _entries;
    private readonly int _capacity;
    private long _dropped;
    private long _totalWritten;

    /// <summary>Creates a buffer with a capacity of 2000 entries.</summary>
    public BoundedLogBuffer()
        : this(2000)
    {
    }

    /// <summary>Creates a buffer with an explicit capacity.</summary>
    public BoundedLogBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _entries = new Queue<LogEntry>(Math.Min(capacity, 1024));
    }

    /// <summary>Gets the maximum number of retained entries.</summary>
    public int Capacity => _capacity;

    /// <summary>Gets the number of currently retained entries.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Gets how many entries were discarded because the buffer was full.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_sync)
            {
                return _dropped;
            }
        }
    }

    /// <summary>Gets how many entries have been written since the buffer was created.</summary>
    public long TotalWritten
    {
        get
        {
            lock (_sync)
            {
                return _totalWritten;
            }
        }
    }

    /// <summary>Adds an entry, dropping the oldest entry when the buffer is full.</summary>
    public void Add(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            while (_entries.Count >= _capacity)
            {
                _entries.Dequeue();
                _dropped++;
            }

            _entries.Enqueue(entry);
            _totalWritten++;
        }
    }

    /// <summary>Returns a stable snapshot ordered oldest to newest.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>Returns the most recent errors, oldest to newest.</summary>
    public IReadOnlyList<LogEntry> RecentErrors(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        List<LogEntry> matches = [];
        lock (_sync)
        {
            foreach (var entry in _entries)
            {
                if (entry.Level >= LogLevel.Error)
                {
                    matches.Add(entry);
                }
            }
        }

        return matches.Count <= max ? matches : matches.GetRange(matches.Count - max, max);
    }

    /// <summary>Removes all retained entries.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
