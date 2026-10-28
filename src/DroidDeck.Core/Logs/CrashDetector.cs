using System.Globalization;

namespace DroidDeck.Core.Logs;

/// <summary>
/// Detects Java/Kotlin crashes, ANRs, and native crashes in a parsed logcat stream.
/// The detector is stateful: a crash header emits one event and following lines are
/// appended to the same event so the banner can copy the full stack.
/// </summary>
public sealed class CrashDetector
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(10);
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private CrashEvent? _active;
    private string? _targetPackage;
    private int? _targetUid;
    private bool _collecting;

    /// <summary>Gets or sets the package to follow. Other packages are ignored.</summary>
    public string? TargetPackage
    {
        get => _targetPackage;
        set => _targetPackage = value;
    }

    /// <summary>Gets or sets the app UID used to ignore unrelated processes.</summary>
    public int? TargetUid
    {
        get => _targetUid;
        set => _targetUid = value;
    }

    /// <summary>Gets the crash currently being collected, if any.</summary>
    public CrashEvent? ActiveCrash
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    /// <summary>Processes one log entry and returns a newly started crash event.</summary>
    public CrashEvent? Process(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            if (entry.IsContinuation)
            {
                AppendToActive(entry);
                return null;
            }

            var crash = Detect(entry);
            if (crash is not null)
            {
                if (IsDuplicate(crash, entry.Timestamp))
                {
                    _collecting = false;
                    return null;
                }

                _recent[crash.DedupeKey] = entry.Timestamp;
                PruneRecent(entry.Timestamp);
                _active = crash;
                _collecting = true;
                return crash;
            }

            if (_collecting && ShouldCollect(entry))
            {
                AppendToActive(entry);
            }
            else if (_collecting)
            {
                _collecting = false;
            }

            return null;
        }
    }

    /// <summary>Stops collecting and returns the last crash.</summary>
    public CrashEvent? Complete()
    {
        lock (_sync)
        {
            _collecting = false;
            var crash = _active;
            _active = null;
            return crash;
        }
    }

    /// <summary>Clears detection state.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _recent.Clear();
            _active = null;
            _collecting = false;
        }
    }

    private CrashEvent? Detect(LogEntry entry)
    {
        if (LogcatParser.TryParseActivityManagerEvent(entry, out var eventName, out var fields))
        {
            // Event log fields start with userId, pid, and processName:
            // am_crash: userId,pid,processName,flags,exception,message,file,line
            // am_anr: userId,pid,processName,reason,flags
            if (eventName == "am_crash" && fields.Count >= 3)
            {
                var packageName = fields[2];
                if (!MatchesTarget(packageName))
                {
                    return null;
                }

                var pid = ParseInt(fields[1]);
                var exception = fields.Count > 4 ? fields[4] : "Crash";
                var message = fields.Count > 5 ? fields[5] : string.Empty;
                var file = fields.Count > 6 ? fields[6] : null;
                var line = fields.Count > 7 ? fields[7] : null;
                var summary = string.IsNullOrWhiteSpace(message) ? exception : $"{exception}: {message}";
                var stack = new List<string> { entry.Raw };
                if (file is not null)
                {
                    stack.Add($"at {file}:{line}");
                }

                return new CrashEvent
                {
                    Kind = CrashKind.Crash,
                    PackageName = packageName,
                    Pid = pid,
                    Summary = summary,
                    StackTrace = string.Join(Environment.NewLine, stack),
                    Timestamp = entry.Timestamp,
                    FirstSequence = entry.Sequence,
                    LastSequence = entry.Sequence,
                    DedupeKey = $"crash:{packageName}:{pid}",
                };
            }

            if (eventName == "am_anr" && fields.Count >= 4)
            {
                var packageName = fields[2];
                if (!MatchesTarget(packageName))
                {
                    return null;
                }

                var pid = ParseInt(fields[1]);
                var reason = fields.Count > 3 ? fields[3] : "Input dispatching timed out";
                return new CrashEvent
                {
                    Kind = CrashKind.Anr,
                    PackageName = packageName,
                    Pid = pid,
                    Summary = $"ANR in {packageName}: {reason}",
                    StackTrace = entry.Raw,
                    Timestamp = entry.Timestamp,
                    FirstSequence = entry.Sequence,
                    LastSequence = entry.Sequence,
                    DedupeKey = $"anr:{packageName}:{pid}",
                };
            }

            return null;
        }

        if (entry.Tag == "AndroidRuntime" &&
            entry.Message.Contains("FATAL EXCEPTION", StringComparison.OrdinalIgnoreCase))
        {
            var packageName = ExtractPackage(entry.Message) ?? _targetPackage;
            if (!MatchesTarget(packageName))
            {
                return null;
            }

            return new CrashEvent
            {
                Kind = CrashKind.Crash,
                PackageName = packageName,
                Pid = entry.Pid,
                Summary = FirstLine(entry.Message, "FATAL EXCEPTION"),
                StackTrace = entry.Raw,
                Timestamp = entry.Timestamp,
                FirstSequence = entry.Sequence,
                LastSequence = entry.Sequence,
                DedupeKey = $"crash:{packageName}:{entry.Pid}",
            };
        }

        if (entry.Tag == "ActivityManager" &&
            entry.Message.Contains("ANR in ", StringComparison.OrdinalIgnoreCase))
        {
            var packageName = ExtractAnrPackage(entry.Message);
            if (!MatchesTarget(packageName))
            {
                return null;
            }

            return new CrashEvent
            {
                Kind = CrashKind.Anr,
                PackageName = packageName,
                Pid = entry.Pid,
                Summary = FirstLine(entry.Message, "ANR in"),
                StackTrace = entry.Raw,
                Timestamp = entry.Timestamp,
                FirstSequence = entry.Sequence,
                LastSequence = entry.Sequence,
                DedupeKey = $"anr:{packageName}:{entry.Pid}",
            };
        }

        if (entry.Tag == "DEBUG" && entry.Message.Contains("*** *** ***", StringComparison.Ordinal))
        {
            return new CrashEvent
            {
                Kind = CrashKind.Native,
                PackageName = _targetPackage,
                Pid = entry.Pid,
                Summary = "Native crash detected",
                StackTrace = entry.Raw,
                Timestamp = entry.Timestamp,
                FirstSequence = entry.Sequence,
                LastSequence = entry.Sequence,
                DedupeKey = $"native:{_targetPackage}:{entry.Pid}",
            };
        }

        return null;
    }

    private void AppendToActive(LogEntry entry)
    {
        var active = _active;
        if (active is null || !_collecting)
        {
            return;
        }

        if (!string.IsNullOrEmpty(active.StackTrace))
        {
            active.StackTrace += Environment.NewLine;
        }

        active.StackTrace += entry.Raw;
        active.LastSequence = entry.Sequence;
    }

    private bool ShouldCollect(LogEntry entry)
    {
        if (entry.Tag is "AndroidRuntime" or "DEBUG" or "ActivityManager" or "crash_dump64" or "libc")
        {
            return true;
        }

        if (entry.Pid is { } pid && _active?.Pid is { } activePid && pid == activePid)
        {
            return true;
        }

        return entry.Message.StartsWith('\t') ||
               entry.Message.StartsWith("at ", StringComparison.Ordinal) ||
               entry.Message.StartsWith("Caused by:", StringComparison.Ordinal) ||
               entry.Message.StartsWith("Reason:", StringComparison.Ordinal) ||
               entry.Message.StartsWith("PID:", StringComparison.Ordinal) ||
               entry.Message.StartsWith("Process:", StringComparison.Ordinal);
    }

    private bool IsDuplicate(CrashEvent crash, DateTimeOffset timestamp)
    {
        if (!_recent.TryGetValue(crash.DedupeKey, out var last))
        {
            return false;
        }

        // A Java crash and its am_crash event share a dedupe key but arrive close together.
        if (crash.Kind == CrashKind.Crash && last != default && timestamp - last < DedupeWindow)
        {
            return true;
        }

        return crash.Kind == CrashKind.Anr && timestamp - last < DedupeWindow;
    }

    private void PruneRecent(DateTimeOffset now)
    {
        foreach (var key in _recent.Where(pair => now - pair.Value > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray())
        {
            _recent.Remove(key);
        }
    }

    private bool MatchesTarget(string? packageName)
    {
        if (string.IsNullOrEmpty(packageName))
        {
            return true;
        }

        return _targetPackage is null ||
               string.Equals(packageName, _targetPackage, StringComparison.Ordinal);
    }

    private static string? ExtractPackage(string message)
    {
        var index = message.IndexOf("Process: ", StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + "Process: ".Length;
        var end = message.IndexOf(',', start);
        return (end < 0 ? message[start..] : message[start..end]).Trim();
    }

    private static string? ExtractAnrPackage(string message)
    {
        var index = message.IndexOf("ANR in ", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + "ANR in ".Length;
        var end = message.IndexOf(' ', start);
        var packageName = (end < 0 ? message[start..] : message[start..end]).Trim();
        return packageName.Length == 0 ? null : packageName;
    }

    private static string FirstLine(string message, string fallback)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? fallback : line;
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
}
