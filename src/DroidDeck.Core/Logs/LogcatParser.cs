using System.Globalization;
using System.Text.RegularExpressions;

namespace DroidDeck.Core.Logs;

/// <summary>Parses Android logcat threadtime lines into structured entries.</summary>
public sealed partial class LogcatParser
{
    private static readonly string[] LogcatBuffers = ["main", "system", "crash", "events"];

    /// <summary>Parses one raw line.</summary>
    public static LogEntry? ParseLine(string raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var line = raw.TrimEnd('\r');
        if (line.StartsWith("--------- beginning of", StringComparison.Ordinal))
        {
            var buffer = line["--------- beginning of ".Length..].Trim();
            return new LogEntry
            {
                Timestamp = now,
                Source = LogSource.Logcat,
                Level = "I",
                Tag = "logcat",
                Message = line,
                Raw = line,
                Buffer = LogcatBuffers.Contains(buffer, StringComparer.OrdinalIgnoreCase) ? buffer : null,
            };
        }

        var match = ThreadTimeRegex().Match(line);
        if (!match.Success)
        {
            return new LogEntry
            {
                Timestamp = now,
                Source = LogSource.Logcat,
                Level = "I",
                Tag = "logcat",
                Message = line,
                Raw = line,
                IsContinuation = true,
            };
        }

        var timestamp = ParseTimestamp(
            match.Groups["date"].Value,
            match.Groups["time"].Value,
            now);
        var level = match.Groups["level"].Value;
        var tag = match.Groups["tag"].Value.Trim();
        var message = match.Groups["message"].Value;
        return new LogEntry
        {
            Timestamp = timestamp,
            Source = LogSource.Logcat,
            Level = level,
            Tag = tag,
            Pid = ParseInt(match.Groups["pid"].Value),
            Tid = ParseInt(match.Groups["tid"].Value),
            Uid = ParseInt(match.Groups["uid"].Value),
            Message = message,
            Raw = line,
        };
    }

    /// <summary>Parses the header of an <c>am_crash</c> or <c>am_anr</c> event line.</summary>
    public static bool TryParseActivityManagerEvent(LogEntry entry, out string eventName, out IReadOnlyList<string> fields)
    {
        eventName = string.Empty;
        fields = [];
        if (entry.Tag is not ("am_crash" or "am_anr" or "am_proc_start" or "am_proc_died" or "am_proc_bound"))
        {
            return false;
        }

        eventName = entry.Tag;
        var message = entry.Message.Trim();
        if (message.StartsWith('[') && message.EndsWith(']'))
        {
            message = message[1..^1];
        }

        fields = message
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(static field => field.Trim('"'))
            .ToArray();
        return true;
    }

    private static DateTimeOffset ParseTimestamp(string date, string time, DateTimeOffset now)
    {
        if (!TimeOnly.TryParse(time, CultureInfo.InvariantCulture, out var timeOfDay))
        {
            return now;
        }

        if (DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fullDate))
        {
            return new DateTimeOffset(fullDate.ToDateTime(timeOfDay), now.Offset);
        }

        if (!DateOnly.TryParseExact(date, "MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var partial))
        {
            return now;
        }

        var year = now.Year;
        var candidate = new DateOnly(year, partial.Month, partial.Day);
        // Logcat lines without a year can straddle New Year's Eve.
        if (candidate > DateOnly.FromDateTime(now.DateTime).AddDays(1))
        {
            candidate = candidate.AddYears(-1);
        }
        else if (candidate < DateOnly.FromDateTime(now.DateTime).AddDays(-300))
        {
            candidate = candidate.AddYears(1);
        }

        return new DateTimeOffset(candidate.ToDateTime(timeOfDay), now.Offset);
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    [GeneratedRegex(
        @"^(?<date>\d{4}-\d{2}-\d{2}|\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}:\d{2}\.\d{3,6})\s+(?:(?<uid>\d+)\s+)?(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEFA])\s+(?<tag>.*?):\s?(?<message>.*)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ThreadTimeRegex();
}

/// <summary>A bounded, thread-safe log buffer that counts dropped lines.</summary>
public sealed class BoundedLogBufferStore
{
    private readonly LogEntry[] _buffer;
    private readonly object _sync = new();
    private int _start;
    private int _count;
    private long _sequence;
    private int _dropped;

    /// <summary>Creates a buffer with a maximum number of entries.</summary>
    public BoundedLogBufferStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _buffer = new LogEntry[capacity];
    }

    /// <summary>Gets the maximum number of entries.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Gets the number of retained entries.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>Gets the number of entries dropped because the buffer was full.</summary>
    public int Dropped
    {
        get
        {
            lock (_sync)
            {
                return _dropped;
            }
        }
    }

    /// <summary>Adds one entry and returns its sequence number.</summary>
    public long Append(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            entry.Sequence = ++_sequence;
            if (_count == _buffer.Length)
            {
                _start = (_start + 1) % _buffer.Length;
                _count--;
                _dropped++;
            }

            var index = (_start + _count) % _buffer.Length;
            _buffer[index] = entry;
            _count++;
            return entry.Sequence;
        }
    }

    /// <summary>Returns a snapshot of the retained entries in order.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            var result = new LogEntry[_count];
            for (var index = 0; index < _count; index++)
            {
                result[index] = _buffer[(_start + index) % _buffer.Length];
            }

            return result;
        }
    }

    /// <summary>Removes all entries without resetting the sequence or drop counter.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_buffer);
            _start = 0;
            _count = 0;
        }
    }

    /// <summary>Adds the dropped counter to the retained count.</summary>
    public int TotalObserved
    {
        get
        {
            lock (_sync)
            {
                return _count + _dropped;
            }
        }
    }
}
