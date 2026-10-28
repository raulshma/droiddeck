using System.Globalization;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Resources;
using DroidDeck.Core.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Screen;

/// <summary>
/// Captures thumbnails with a throttled device-side <c>screencap</c> and an
/// <c>adb pull</c>. Captures are serialized so a device list cannot start a burst
/// of adb transfers, and devices that are already mirroring are skipped.
/// </summary>
public sealed class AdbThumbnailService : IThumbnailService, IDisposable
{
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(15);

    private readonly IProcessRunner _runner;
    private readonly IScreenService _screens;
    private readonly Func<string> _adbPathProvider;
    private readonly ILogger<AdbThumbnailService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly string _stagingDirectory;
    private readonly IResourceGuard? _guard;

    /// <summary>Creates a thumbnail service.</summary>
    public AdbThumbnailService(
        IProcessRunner runner,
        IScreenService screens,
        Func<string> adbPathProvider,
        string? stagingDirectory = null,
        TimeProvider? timeProvider = null,
        ILogger<AdbThumbnailService>? logger = null,
        IResourceGuard? resourceGuard = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AdbThumbnailService>.Instance;
        _stagingDirectory = stagingDirectory
            ?? Path.Combine(Path.GetTempPath(), "DroidDeck", "thumbnails");
        _guard = resourceGuard;
    }

    /// <inheritdoc />
    public async Task<ScreenThumbnail> CaptureAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IAsyncDisposable? lease = null;
        try
        {
            if (_guard is not null)
            {
                lease = await _guard.AcquireAsync(ResourceKind.Thumbnail, serial, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CaptureCoreAsync(serial, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            _captureGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RunAsync(
        IReadOnlyCollection<string> serials,
        TimeSpan interval,
        Func<ScreenThumbnail, Task> onThumbnail,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serials);
        ArgumentNullException.ThrowIfNull(onThumbnail);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The thumbnail interval must be positive.");
        }

        if (serials.Count == 0)
        {
            return;
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var activeMirrors = _screens.ActiveSessions
                .Select(static session => session.Serial)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var serial in serials.Distinct(StringComparer.Ordinal))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (activeMirrors.Contains(serial))
                {
                    continue;
                }

                try
                {
                    var thumbnail = await CaptureAsync(serial, cancellationToken).ConfigureAwait(false);
                    await onThumbnail(thumbnail).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "A thumbnail could not be captured for {Serial}.", serial);
                }
            }

            try
            {
                await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _captureGate.Dispose();

    private async Task<ScreenThumbnail> CaptureCoreAsync(string serial, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_stagingDirectory);
        var stamp = _timeProvider.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var fileName = $"thumb-{Sanitize(serial)}-{stamp}.png";
        var localPath = Path.Combine(_stagingDirectory, fileName);
        var remotePath = $"/sdcard/DroidDeck/{fileName}";

        try
        {
            var prepare = await RunAdbAsync(
                ["-s", serial, "shell", "mkdir", "-p", "/sdcard/DroidDeck"],
                cancellationToken).ConfigureAwait(false);
            if (prepare.ExitCode != 0)
            {
                throw Failure(serial, "create the thumbnail directory", prepare);
            }

            var capture = await RunAdbAsync(
                ["-s", serial, "shell", "screencap", "-p", remotePath],
                cancellationToken).ConfigureAwait(false);
            if (capture.ExitCode != 0)
            {
                throw Failure(serial, "capture the thumbnail", capture);
            }

            var pull = await RunAdbAsync(
                ["-s", serial, "pull", remotePath, localPath],
                cancellationToken).ConfigureAwait(false);
            if (pull.ExitCode != 0 || !File.Exists(localPath))
            {
                throw Failure(serial, "copy the thumbnail", pull);
            }

            var png = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
            if (png.Length == 0)
            {
                throw new InvalidOperationException($"The thumbnail for {serial} was empty.");
            }

            return new ScreenThumbnail
            {
                Serial = serial,
                Png = png,
                CapturedAt = _timeProvider.GetUtcNow(),
                CommandLine = pull.CommandLine,
            };
        }
        finally
        {
            TryDelete(localPath);
            _ = await RunAdbAsync(
                ["-s", serial, "shell", "rm", "-f", remotePath],
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ProcessResult> RunAdbAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await _runner.RunAsync(
            new ProcessRequest
            {
                FileName = _adbPathProvider(),
                Arguments = arguments,
                Timeout = CaptureTimeout,
                MaxOutputCharacters = 64 * 1024,
            },
            output: null,
            cancellationToken).ConfigureAwait(false);

    private static InvalidOperationException Failure(string serial, string action, ProcessResult result)
    {
        var detail = FirstLine(result.CombinedOutput);
        return new InvalidOperationException(
            $"Could not {action} for {serial}: {detail}. " +
            "Check that the device is connected, authorized, and has free storage.");
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no output";
        }

        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 240 ? line : line[..240] + "...";
    }

    private static string Sanitize(string serial) =>
        new(serial.Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
