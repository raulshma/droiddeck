using DroidDeck.Core.Processes;

namespace DroidDeck.Screen;

/// <summary>Per-stage timeouts for a remote capture sequence.</summary>
internal sealed record RemoteCaptureTimeouts
{
    /// <summary>Gets the timeout for creating the remote directory.</summary>
    public TimeSpan Prepare { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the timeout for the device-side capture.</summary>
    public TimeSpan Capture { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the timeout for copying the capture locally.</summary>
    public TimeSpan Pull { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the timeout for removing the remote file.</summary>
    public TimeSpan Cleanup { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>Describes one capture from a device screen to a local file.</summary>
internal sealed record RemoteCaptureRequest
{
    /// <summary>Gets the device serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets the local file that receives the capture.</summary>
    public required string LocalPath { get; init; }

    /// <summary>Gets the file name used inside the remote directory.</summary>
    public required string RemoteFileName { get; init; }

    /// <summary>Gets the remote staging directory.</summary>
    public string RemoteDirectory { get; init; } = RemoteScreenCapture.DefaultRemoteDirectory;

    /// <summary>Gets the noun used in failure messages, such as <c>screenshot</c>.</summary>
    public string Operation { get; init; } = "screenshot";

    /// <summary>Gets the maximum number of captured characters per stream.</summary>
    public int MaxOutputCharacters { get; init; } = 512 * 1024;

    /// <summary>Gets the per-stage timeouts.</summary>
    public RemoteCaptureTimeouts Timeouts { get; init; } = new();
}

/// <summary>The outcome of one remote capture sequence.</summary>
internal sealed record RemoteCaptureResult
{
    /// <summary>Gets whether the capture was copied locally.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets a user-facing failure message.</summary>
    public string? Message { get; init; }

    /// <summary>Gets a single-line detail of the failed command output.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the command line of the failed or successful pull step.</summary>
    public string? CommandLine { get; init; }
}

/// <summary>
/// Owns the create, capture, pull, and clean-up sequence used for device
/// screenshots and thumbnails, including remote file naming and failure wording.
/// </summary>
internal sealed class RemoteScreenCapture
{
    /// <summary>The directory used for transient files on the device.</summary>
    internal const string DefaultRemoteDirectory = "/sdcard/DroidDeck";

    private readonly IProcessRunner _runner;
    private readonly Func<string> _adbPathProvider;

    /// <summary>Creates a capture sequence for the current adb executable.</summary>
    public RemoteScreenCapture(IProcessRunner runner, Func<string> adbPathProvider)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
    }

    /// <summary>Builds a safe remote file name from a serial and timestamp.</summary>
    public static string CreateFileName(
        string prefix,
        string serial,
        string stamp,
        string extension,
        bool trimDashes = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(stamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        var normalizedExtension = extension.StartsWith('.') ? extension : "." + extension;
        return $"{prefix}-{Sanitize(serial, trimDashes)}-{stamp}{normalizedExtension}";
    }

    /// <summary>Replaces characters that are unsafe in a remote file name.</summary>
    public static string Sanitize(string value, bool trimDashes = true)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sanitized = new string(
            value.Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '-')
                .ToArray());
        return trimDashes ? sanitized.Trim('-') : sanitized;
    }

    /// <summary>Runs the capture sequence and always removes the remote file.</summary>
    public async Task<RemoteCaptureResult> CaptureAsync(
        RemoteCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LocalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteFileName);

        var remoteDirectory = string.IsNullOrWhiteSpace(request.RemoteDirectory)
            ? DefaultRemoteDirectory
            : request.RemoteDirectory.TrimEnd('/');
        var remotePath = remoteDirectory + "/" + request.RemoteFileName.TrimStart('/');

        var prepare = await RunAsync(
                request,
                ["shell", "mkdir", "-p", remoteDirectory],
                request.Timeouts.Prepare,
                cancellationToken)
            .ConfigureAwait(false);
        if (prepare.ExitCode != 0)
        {
            return Failure(
                $"The {request.Operation} directory could not be created on {request.Serial}.",
                prepare);
        }

        try
        {
            var capture = await RunAsync(
                    request,
                    ["shell", "screencap", "-p", remotePath],
                    request.Timeouts.Capture,
                    cancellationToken)
                .ConfigureAwait(false);
            if (capture.ExitCode != 0)
            {
                return Failure(
                    $"The {request.Operation} failed on {request.Serial}.",
                    capture);
            }

            var pull = await RunAsync(
                    request,
                    ["pull", remotePath, request.LocalPath],
                    request.Timeouts.Pull,
                    cancellationToken)
                .ConfigureAwait(false);
            if (pull.ExitCode != 0 || !File.Exists(request.LocalPath))
            {
                return Failure(
                    $"The {request.Operation} could not be copied from {request.Serial}.",
                    pull);
            }

            return new RemoteCaptureResult
            {
                Succeeded = true,
                CommandLine = pull.CommandLine,
            };
        }
        finally
        {
            _ = await RunAsync(
                    request,
                    ["shell", "rm", "-f", remotePath],
                    request.Timeouts.Cleanup,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private Task<ProcessResult> RunAsync(
        RemoteCaptureRequest request,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return _runner.RunAsync(
            new ProcessRequest
            {
                FileName = _adbPathProvider(),
                Arguments = ["-s", request.Serial, .. arguments],
                Timeout = timeout,
                MaxOutputCharacters = request.MaxOutputCharacters,
            },
            output: null,
            cancellationToken);
    }

    private static RemoteCaptureResult Failure(
        string message,
        ProcessResult result) => new()
        {
            Succeeded = false,
            Message = message,
            Detail = Summarize(result.CombinedOutput),
            CommandLine = result.CommandLine,
        };

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "no output";
        }

        var singleLine = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 300 ? singleLine : singleLine[..300] + "...";
    }
}
