namespace DroidDeck.Core.Toolbox;

/// <summary>An entry in a device directory listing.</summary>
public sealed record DeviceFileEntry
{
    /// <summary>Gets the file or directory name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the absolute path on the device.</summary>
    public required string FullPath { get; init; }

    /// <summary>Gets whether the entry is a directory.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Gets the size in bytes, or zero for directories.</summary>
    public long Size { get; init; }

    /// <summary>Gets the last modification time when it could be parsed.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    /// <summary>Gets the raw permission string, such as <c>drwxrwx--x</c>.</summary>
    public string? Permissions { get; init; }

    /// <summary>Gets a display size such as <c>1.2 MB</c>.</summary>
    public string SizeLabel => IsDirectory ? "Folder" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>Progress reported while a file is transferred.</summary>
/// <param name="Phase">A short phase label, such as <c>Transferring</c>.</param>
/// <param name="Percent">The completion percentage when known.</param>
/// <param name="Detail">The latest raw output line.</param>
public sealed record FileTransferProgress(string Phase, double? Percent, string? Detail)
{
    /// <summary>Gets whether a determinate percentage is available.</summary>
    public bool IsDeterminate => Percent is not null;
}

/// <summary>Browses devices and pushes, pulls, and deletes files.</summary>
public interface IDeviceFileService
{
    /// <summary>Lists a device directory.</summary>
    Task<IReadOnlyList<DeviceFileEntry>> ListAsync(
        string serial,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a local file or directory to the device.</summary>
    Task<ToolboxCommandResult> PushAsync(
        string serial,
        string localPath,
        string remotePath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a device file or directory to this computer.</summary>
    Task<ToolboxCommandResult> PullAsync(
        string serial,
        string remotePath,
        string localPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a device file or directory. Requires explicit confirmation.</summary>
    Task<ToolboxCommandResult> DeleteAsync(
        string serial,
        string remotePath,
        bool isDirectory,
        bool confirmed,
        CancellationToken cancellationToken = default);
}
