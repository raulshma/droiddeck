using System.Text;
using System.Text.Json;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Runs;

namespace DroidDeck.Core.Sessions;

/// <summary>A compact view of a persisted session.</summary>
public sealed class SessionSummary
{
    /// <summary>Gets or sets the session identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the project name.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Gets or sets the run configuration name.</summary>
    public string ConfigurationName { get; set; } = string.Empty;

    /// <summary>Gets or sets the device serial.</summary>
    public string DeviceSerial { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the final state.</summary>
    public RunSessionState State { get; set; }

    /// <summary>Gets or sets when the session started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Gets or sets when the session ended.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Gets or sets the duration.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets or sets the number of steps.</summary>
    public int StepCount { get; set; }

    /// <summary>Gets or sets the final error.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the folder that contains the session files.</summary>
    public string? DirectoryPath { get; set; }
}

/// <summary>The format used to export a session.</summary>
public enum SessionExportFormat
{
    /// <summary>A readable text report.</summary>
    Text,

    /// <summary>A JSON document.</summary>
    Json,
}

/// <summary>The result of applying retention.</summary>
public sealed class RetentionResult
{
    /// <summary>Gets or sets the number of removed sessions.</summary>
    public int RemovedSessions { get; set; }

    /// <summary>Gets or sets the number of bytes removed.</summary>
    public long RemovedBytes { get; set; }

    /// <summary>Gets or sets the removed session names.</summary>
    public List<string> Removed { get; set; } = [];

    /// <summary>Gets or sets the explanation shown in the UI.</summary>
    public string Summary => RemovedSessions == 0
        ? "No sessions were removed."
        : $"Removed {RemovedSessions} session{(RemovedSessions == 1 ? string.Empty : "s")} ({RemovedBytes / 1024.0 / 1024.0:F1} MB) to respect retention settings.";
}

/// <summary>Persists session summaries, steps, and logs.</summary>
public interface ISessionStore
{
    /// <summary>Writes a session to disk.</summary>
    Task SaveAsync(RunSession session, CancellationToken cancellationToken = default);

    /// <summary>Loads a session read-only.</summary>
    Task<RunSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Lists session summaries, newest first.</summary>
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Exports a session as text or JSON.</summary>
    Task<string> ExportAsync(
        RunSession session,
        SessionExportFormat format,
        bool redact = true,
        CancellationToken cancellationToken = default);

    /// <summary>Removes sessions older than the age limit and then trims the total size.</summary>
    Task<RetentionResult> ApplyRetentionAsync(
        int retentionDays,
        long maxBytes,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one session.</summary>
    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Stores sessions as one folder per run.</summary>
public sealed class JsonSessionStore : ISessionStore
{
    private const string MetaFileName = "meta.json";
    private const string ActivityFileName = "activity.jsonl";
    private const string BuildLogFileName = "build.log";
    private const string LogcatFileName = "logcat.log";
    private readonly AppPaths _paths;

    /// <summary>Creates a store under the supplied data paths.</summary>
    public JsonSessionStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    /// <inheritdoc />
    public async Task SaveAsync(RunSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var directory = GetSessionDirectory(session);
        Directory.CreateDirectory(directory);

        var metaPath = Path.Combine(directory, MetaFileName);
        await AtomicFile.WriteAllTextAsync(metaPath, DroidDeckJson.Serialize(session), keepBackup: false, cancellationToken)
            .ConfigureAwait(false);

        var activity = new StringBuilder();
        foreach (var step in session.Steps)
        {
            activity.Append(DroidDeckJson.Serialize(new
            {
                type = "step",
                step.Id,
                step.Name,
                state = step.State.ToString(),
                step.StartedAt,
                step.EndedAt,
                step.Duration,
                step.CommandLine,
                step.ExitCode,
                step.Error,
                step.SkipReason,
            })).Append('\n');
        }

        foreach (var crash in session.Crashes)
        {
            activity.Append(DroidDeckJson.Serialize(new
            {
                type = "crash",
                crash.Kind,
                crash.PackageName,
                crash.Pid,
                crash.Summary,
                crash.Timestamp,
                crash.StackTrace,
            })).Append('\n');
        }

        await File.WriteAllTextAsync(
                Path.Combine(directory, ActivityFileName),
                activity.ToString(),
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllLinesAsync(
                Path.Combine(directory, BuildLogFileName),
                session.BuildOutput,
                cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllLinesAsync(
                Path.Combine(directory, LogcatFileName),
                session.LogEntries.Select(static entry => entry.Raw),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var directory = FindSessionDirectory(sessionId);
        if (directory is null)
        {
            return null;
        }

        var metaPath = Path.Combine(directory, MetaFileName);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
        var session = DroidDeckJson.Deserialize<RunSession>(json);
        if (session is null)
        {
            return null;
        }

        session.IsReadOnly = true;
        var buildLog = Path.Combine(directory, BuildLogFileName);
        if (File.Exists(buildLog))
        {
            session.BuildOutput = (await File.ReadAllLinesAsync(buildLog, cancellationToken).ConfigureAwait(false)).ToList();
        }

        var logcatLog = Path.Combine(directory, LogcatFileName);
        if (File.Exists(logcatLog))
        {
            var now = session.StartedAt;
            var entries = new List<DroidDeck.Core.Logs.LogEntry>();
            var sequence = 0L;
            foreach (var line in await File.ReadAllLinesAsync(logcatLog, cancellationToken).ConfigureAwait(false))
            {
                var entry = LogcatParser.ParseLine(line, now);
                if (entry is null)
                {
                    continue;
                }

                entry.Sequence = ++sequence;
                entries.Add(entry);
            }

            session.LogEntries = entries;
        }

        return session;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_paths.SessionsDirectory))
        {
            return [];
        }

        var summaries = new List<SessionSummary>();
        foreach (var directory in Directory.EnumerateDirectories(_paths.SessionsDirectory))
        {
            var metaPath = Path.Combine(directory, MetaFileName);
            if (!File.Exists(metaPath))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(metaPath, cancellationToken).ConfigureAwait(false);
                var session = DroidDeckJson.Deserialize<RunSession>(json);
                if (session is null)
                {
                    continue;
                }

                summaries.Add(new SessionSummary
                {
                    Id = session.Id,
                    ProjectName = session.ProjectName,
                    ConfigurationName = session.ConfigurationName,
                    DeviceSerial = session.DeviceSerial,
                    DeviceName = session.DeviceName,
                    State = session.State,
                    StartedAt = session.StartedAt,
                    EndedAt = session.EndedAt,
                    Duration = session.Duration,
                    StepCount = session.Steps.Count,
                    Error = session.Error,
                    DirectoryPath = directory,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A corrupt session must not break the history list.
            }
        }

        return summaries
            .OrderByDescending(summary => summary.StartedAt)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(
        RunSession session,
        SessionExportFormat format,
        bool redact = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (format == SessionExportFormat.Json)
        {
            var copy = DroidDeckJson.Deserialize<RunSession>(DroidDeckJson.Serialize(session))
                ?? throw new InvalidOperationException("The session could not be exported.");
            if (redact)
            {
                foreach (var step in copy.Steps)
                {
                    step.OutputTail = Redactor.Redact(step.OutputTail);
                    step.Error = Redactor.Redact(step.Error);
                }

                foreach (var entry in copy.LogEntries)
                {
                    entry.Message = Redactor.Redact(entry.Message);
                    entry.Raw = Redactor.Redact(entry.Raw);
                }

                foreach (var crash in copy.Crashes)
                {
                    crash.StackTrace = Redactor.Redact(crash.StackTrace);
                }
            }

            return DroidDeckJson.Serialize(copy);
        }

        var builder = new StringBuilder();
        builder.AppendLine($"DroidDeck session {session.Id}");
        builder.AppendLine($"Project: {session.ProjectName} ({session.ProjectRoot})");
        builder.AppendLine($"Configuration: {session.ConfigurationName}");
        builder.AppendLine($"Device: {session.DeviceName ?? session.DeviceSerial}");
        builder.AppendLine($"State: {session.State}");
        builder.AppendLine($"Started: {session.StartedAt:O}");
        builder.AppendLine($"Ended: {session.EndedAt:O}");
        builder.AppendLine($"Duration: {session.Duration}");
        if (!string.IsNullOrEmpty(session.ArtifactPath))
        {
            builder.AppendLine($"Artifact: {session.ArtifactPath}");
        }

        if (!string.IsNullOrEmpty(session.PackageName))
        {
            builder.AppendLine($"Package: {session.PackageName}");
        }

        builder.AppendLine();
        builder.AppendLine("Steps");
        foreach (var step in session.Steps)
        {
            builder.AppendLine($"  [{step.State}] {step.Name} ({step.Duration})");
            if (!string.IsNullOrEmpty(step.CommandLine))
            {
                builder.AppendLine($"    $ {step.CommandLine}");
            }

            if (!string.IsNullOrEmpty(step.Error))
            {
                builder.AppendLine($"    {step.Error}");
            }
        }

        if (session.Crashes.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Crashes");
            foreach (var crash in session.Crashes)
            {
                builder.AppendLine($"  [{crash.Kind}] {crash.Summary}");
                builder.AppendLine(crash.StackTrace);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Build output");
        foreach (var line in session.BuildOutput)
        {
            builder.AppendLine(line);
        }

        builder.AppendLine();
        builder.AppendLine("Logcat");
        foreach (var entry in session.LogEntries)
        {
            builder.AppendLine(entry.Raw);
        }

        var text = builder.ToString();
        return redact ? Redactor.Redact(text) : text;
    }

    /// <inheritdoc />
    public Task<RetentionResult> ApplyRetentionAsync(
        int retentionDays,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var result = new RetentionResult();
        if (!Directory.Exists(_paths.SessionsDirectory))
        {
            return Task.FromResult(result);
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, retentionDays));
        var directories = Directory
            .EnumerateDirectories(_paths.SessionsDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderBy(info => info.LastWriteTimeUtc)
            .ToList();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.LastWriteTimeUtc <= cutoff)
            {
                Remove(directory, result);
            }
        }

        var remaining = Directory
            .EnumerateDirectories(_paths.SessionsDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderBy(info => info.LastWriteTimeUtc)
            .ToList();
        var totalBytes = remaining.Sum(GetSize);
        foreach (var directory in remaining)
        {
            if (totalBytes <= maxBytes || maxBytes <= 0)
            {
                break;
            }

            totalBytes -= GetSize(directory);
            Remove(directory, result);
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var directory = FindSessionDirectory(sessionId);
        if (directory is not null)
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static void Remove(DirectoryInfo directory, RetentionResult result)
    {
        try
        {
            var size = GetSize(directory);
            Directory.Delete(directory.FullName, recursive: true);
            result.RemovedSessions++;
            result.RemovedBytes += size;
            result.Removed.Add(directory.Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort; the next launch retries.
        }
    }

    private static long GetSize(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private string GetSessionDirectory(RunSession session)
    {
        var existing = FindSessionDirectory(session.Id);
        if (existing is not null)
        {
            return existing;
        }

        var name = $"{session.StartedAt:yyyyMMdd-HHmmss}-{Safe(session.ProjectName)}-{Safe(session.DeviceSerial)}-{session.Id[..Math.Min(8, session.Id.Length)]}";
        return Path.Combine(_paths.SessionsDirectory, name);
    }

    private string? FindSessionDirectory(string sessionId)
    {
        if (!Directory.Exists(_paths.SessionsDirectory))
        {
            return null;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.SessionsDirectory))
        {
            if (directory.EndsWith("-" + sessionId[..Math.Min(8, sessionId.Length)], StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(directory, MetaFileName)))
            {
                return directory;
            }
        }

        return null;
    }

    private static string Safe(string value)
    {
        var safe = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');
        return safe.Length == 0 ? "session" : safe;
    }
}
