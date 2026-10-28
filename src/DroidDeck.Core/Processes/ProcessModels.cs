using System.Text;

namespace DroidDeck.Core.Processes;

/// <summary>Identifies the stream a process line came from.</summary>
public enum ProcessOutputChannel
{
    /// <summary>Standard output.</summary>
    StandardOutput,

    /// <summary>Standard error.</summary>
    StandardError,
}

/// <summary>A single line of process output with its origin and timestamp.</summary>
public sealed record ProcessOutputLine(ProcessOutputChannel Stream, string Text, DateTimeOffset Timestamp);

/// <summary>
/// Describes one process invocation. Arguments are always passed as a list and never
/// concatenated into a shell command.
/// </summary>
public sealed record ProcessRequest
{
    /// <summary>Gets the executable to start.</summary>
    public required string FileName { get; init; }

    /// <summary>Gets the argument list.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets the working directory, or <see langword="null"/> to inherit it.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets environment variables to add or replace.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the optional timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets the maximum number of characters captured per stream.</summary>
    public int MaxOutputCharacters { get; init; } = 4 * 1024 * 1024;

    /// <summary>Gets whether ANSI escape sequences are removed from captured output.</summary>
    public bool StripAnsi { get; init; } = true;

    /// <summary>
    /// Gets whether a bare carriage return also terminates a line. adb uses
    /// carriage returns for push and pull progress updates.
    /// </summary>
    public bool SplitOnCarriageReturn { get; init; }

    /// <summary>Gets optional text written to standard input before it is closed.</summary>
    public string? StandardInput { get; init; }

    /// <summary>Gets whether a console window is suppressed. Defaults to <see langword="true"/>.</summary>
    public bool CreateNoWindow { get; init; } = true;

    /// <summary>
    /// Gets whether the process tree is placed in a job object. Disable this for
    /// commands that intentionally leave a background server running.
    /// </summary>
    public bool UseJobObject { get; init; } = true;

    /// <summary>Creates a request with a list of arguments.</summary>
    public static ProcessRequest Create(string fileName, params string[] arguments) => new()
    {
        FileName = fileName,
        Arguments = arguments,
    };

    /// <summary>Formats the command for display and logging without executing a shell.</summary>
    public string ToCommandLine() => FormatCommandLine(FileName, Arguments);

    /// <summary>Formats an executable and arguments using Windows command-line quoting rules.</summary>
    public static string FormatCommandLine(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var builder = new StringBuilder(Quote(fileName));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(static c => !char.IsWhiteSpace(c) && c != '"' && c != '\\'))
        {
            return value;
        }

        if (value.Length == 0)
        {
            return "\"\"";
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }

        return builder.Append('"').ToString();
    }
}

/// <summary>The outcome of a process invocation.</summary>
public sealed record ProcessResult
{
    /// <summary>Gets the displayed command line.</summary>
    public required string CommandLine { get; init; }

    /// <summary>Gets the exit code, or -1 when the process did not start.</summary>
    public int ExitCode { get; init; } = -1;

    /// <summary>Gets captured standard output.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Gets captured standard error.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>Gets whether the process exceeded its timeout and was terminated.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Gets whether the caller canceled the run.</summary>
    public bool WasCanceled { get; init; }

    /// <summary>Gets whether output exceeded the configured limit.</summary>
    public bool OutputTruncated { get; init; }

    /// <summary>Gets a start failure description, when the process could not be launched.</summary>
    public string? StartError { get; init; }

    /// <summary>Gets how long the run took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets whether the process ran to completion with exit code zero.</summary>
    public bool Succeeded => StartError is null && !TimedOut && !WasCanceled && ExitCode == 0;

    /// <summary>Combines both streams for diagnostics.</summary>
    public string CombinedOutput => string.IsNullOrEmpty(StandardError)
        ? StandardOutput
        : string.IsNullOrEmpty(StandardOutput)
            ? StandardError
            : StandardOutput + Environment.NewLine + StandardError;
}

/// <summary>Runs child processes with structured output, timeouts, and tree cleanup.</summary>
public interface IProcessRunner
{
    /// <summary>Runs a process to completion.</summary>
    /// <param name="request">The process to run.</param>
    /// <param name="output">Receives output lines as they arrive.</param>
    /// <param name="cancellationToken">Cancels the run and terminates the process tree.</param>
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default);
}
