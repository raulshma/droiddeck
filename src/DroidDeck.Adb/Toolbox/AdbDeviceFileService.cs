using System.Globalization;
using DroidDeck.Adb.Commands;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Toolbox;

/// <summary>Browses and transfers files with adb push, pull, ls, and rm.</summary>
public sealed class AdbDeviceFileService : IDeviceFileService
{
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(30);

    private readonly Func<string> _adbPathProvider;
    private readonly IDeviceOperationCoordinator? _coordinator;
    private readonly ILogger<AdbDeviceFileService> _logger;
    private readonly AdbCommandExecutor _adb;

    /// <summary>Creates a file service with a provider for the current adb executable.</summary>
    public AdbDeviceFileService(
        IProcessRunner runner,
        Func<string> adbPathProvider,
        ILogger<AdbDeviceFileService>? logger = null,
        IDeviceOperationCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _logger = logger ?? NullLogger<AdbDeviceFileService>.Instance;
        _coordinator = coordinator;
        _adb = new AdbCommandExecutor(runner, _adbPathProvider, logger: _logger);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceFileEntry>> ListAsync(
        string serial,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var remotePath = NormalizeRemotePath(path);
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                ["shell", "ls", "-la", QuoteShellArgument(remotePath)],
                ListingTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Listing {Path} on {Serial} failed: {Output}", remotePath, serial, result.CombinedOutput);
            return [];
        }

        return ParseListing(remotePath, result.StandardOutput);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> PushAsync(
        string serial,
        string localPath,
        string remotePath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
        {
            return ToolboxCommandResult.Failure(
                $"The local path '{localPath}' does not exist.");
        }

        await using var lease = await AcquireDeviceLockAsync(
                serial,
                $"Push {Path.GetFileName(localPath)} to {remotePath}",
                cancellationToken)
            .ConfigureAwait(false);
        return await TransferAsync(
            serial,
            ["push", "-p", Path.GetFullPath(localPath), remotePath],
            progress,
            $"Pushed {Path.GetFileName(localPath)} to {remotePath}.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> PullAsync(
        string serial,
        string remotePath,
        string localPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        await using var lease = await AcquireDeviceLockAsync(
                serial,
                $"Pull {remotePath} to {Path.GetFileName(localPath)}",
                cancellationToken)
            .ConfigureAwait(false);
        return await TransferAsync(
            serial,
            ["pull", "-p", remotePath, Path.GetFullPath(localPath)],
            progress,
            $"Pulled {remotePath} to {localPath}.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> DeleteAsync(
        string serial,
        string remotePath,
        bool isDirectory,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        var normalized = NormalizeRemotePath(remotePath);
        if (normalized is "/" or "." or ".." || normalized.Length < 2)
        {
            return ToolboxCommandResult.Failure("Refusing to delete a root or empty device path.");
        }

        var arguments = new List<string>
        {
            "shell", "rm", "-f",
        };
        if (isDirectory)
        {
            arguments.Add("-r");
        }

        arguments.Add(QuoteShellArgument(normalized));
        var display = ProcessRequest.FormatCommandLine(
            _adbPathProvider(),
            AdbCommandExecutor.BuildDeviceArguments(serial, arguments));
        if (!confirmed)
        {
            return ToolboxCommandResult.Failure(
                $"Deleting {normalized} requires confirmation.",
                display,
                blocked: true) with
            {
                Blocked = true,
                IsDestructive = true,
            };
        }

        await using var lease = await AcquireDeviceLockAsync(serial, $"Delete {normalized}", cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(serial, arguments, ListingTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? $"Deleted {normalized}."
                : $"Could not delete {normalized}: {Summarize(result.CombinedOutput)}",
            isDestructive: true);
    }

    private async Task<IAsyncDisposable?> AcquireDeviceLockAsync(
        string serial,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return null;
        }

        return await _coordinator.AcquireAsync(serial, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ToolboxCommandResult> TransferAsync(
        string serial,
        IReadOnlyList<string> arguments,
        IProgress<FileTransferProgress>? progress,
        string successMessage,
        CancellationToken cancellationToken)
    {
        progress?.Report(new FileTransferProgress("Starting", null, null));
        var output = new ActionProgress<ProcessOutputLine>(line =>
            progress?.Report(new FileTransferProgress("Transferring", ParsePercent(line.Text), line.Text)));
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                arguments,
                TransferTimeout,
                new AdbCommandOptions
                {
                    Output = output,
                    SplitOnCarriageReturn = true,
                },
                cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new FileTransferProgress(
            result.Succeeded ? "Complete" : result.WasCanceled ? "Canceled" : "Failed",
            result.Succeeded ? 100 : null,
            result.CombinedOutput));
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? successMessage
                : result.WasCanceled
                    ? "The transfer was canceled."
                    : $"The transfer failed: {Summarize(result.CombinedOutput)}");
    }

    /// <summary>Parses the output of <c>ls -la</c> for a device directory.</summary>
    public static IReadOnlyList<DeviceFileEntry> ParseListing(string path, string output)
    {
        var entries = new List<DeviceFileEntry>();
        var parent = NormalizeRemotePath(path);
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split(
                [' ', '\t'],
                8,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 8)
            {
                continue;
            }

            var permissions = tokens[0];
            if (permissions.Length < 10 ||
                permissions[0] is not ('d' or '-' or 'l' or 'c' or 'b' or 's' or 'p'))
            {
                continue;
            }

            var name = tokens[7];
            var linkIndex = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (linkIndex >= 0)
            {
                name = name[..linkIndex];
            }

            if (name is "." or "..")
            {
                continue;
            }

            _ = long.TryParse(tokens[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            var modified = ParseTimestamp(tokens[5] + " " + tokens[6]);
            entries.Add(new DeviceFileEntry
            {
                Name = name,
                FullPath = Combine(parent, name),
                IsDirectory = permissions[0] == 'd',
                Size = size,
                Permissions = permissions,
                ModifiedAt = modified,
            });
        }

        return entries
            .OrderByDescending(static entry => entry.IsDirectory)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static DateTimeOffset? ParseTimestamp(string text)
    {
        string[] formats =
        [
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd HH:mm:ss",
            "MMM d HH:mm",
            "MMM d yyyy",
        ];
        if (DateTimeOffset.TryParseExact(
                text,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out parsed)
            ? parsed
            : null;
    }

    private static double? ParsePercent(string text)
    {
        var percentIndex = text.IndexOf('%');
        if (percentIndex <= 0)
        {
            return null;
        }

        var start = percentIndex - 1;
        while (start >= 0 && char.IsAsciiDigit(text[start]))
        {
            start--;
        }

        if (start == percentIndex - 1)
        {
            return null;
        }

        return int.TryParse(
            text.AsSpan(start + 1, percentIndex - start - 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var percent)
            ? Math.Clamp(percent, 0, 100)
            : null;
    }

    private static string NormalizeRemotePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Trim();
        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized.Length == 0 ? "/" : normalized;
    }

    private static string Combine(string parent, string name)
    {
        var basePath = NormalizeRemotePath(parent);
        return basePath == "/" ? "/" + name : basePath + "/" + name;
    }

    private static string QuoteShellArgument(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Device paths cannot contain control characters.", nameof(value));
        }

        return value.Length > 0 && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '_' or '-' or ':' or '@')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "no output";
        }

        var line = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 300 ? line : line[..300] + "...";
    }

    private sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
