using System.Text;

namespace DroidDeck.Core.Infrastructure;

/// <summary>
/// Writes files atomically and keeps a restorable backup.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes text using a temporary file and an atomic replace, preserving the previous
    /// contents in a <c>.bak</c> file.
    /// </summary>
    public static async Task WriteAllTextAsync(
        string path,
        string content,
        bool keepBackup = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryPath, content, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(path))
            {
                if (keepBackup)
                {
                    var backupPath = path + ".bak";
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>
/// Moves unreadable files aside so the application can recover with safe defaults.
/// </summary>
public static class CorruptFileQuarantine
{
    /// <summary>Moves a file into a quarantine directory and returns its new path.</summary>
    public static string Quarantine(string path, string quarantineDirectory, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(quarantineDirectory);

        var fileName = Path.GetFileName(path);
        var stamp = timestamp.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff");
        var destination = Path.Combine(quarantineDirectory, $"{fileName}.{stamp}.corrupt");
        var suffix = 1;
        while (File.Exists(destination))
        {
            destination = Path.Combine(quarantineDirectory, $"{fileName}.{stamp}.{suffix++}.corrupt");
        }

        File.Move(path, destination);
        return destination;
    }
}
