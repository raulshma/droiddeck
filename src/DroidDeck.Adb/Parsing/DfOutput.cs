using System.Globalization;

namespace DroidDeck.Adb.Parsing;

/// <summary>One data row from <c>df -k</c> output, measured in 1K blocks.</summary>
/// <param name="MountPoint">The mount point, or <see langword="null"/> when the output omits it.</param>
internal readonly record struct DfRow(string? MountPoint, long TotalKilobytes, long AvailableKilobytes)
{
    /// <summary>Gets the total size in bytes.</summary>
    internal long TotalBytes => TotalKilobytes * 1024;

    /// <summary>Gets the available size in bytes.</summary>
    internal long AvailableBytes => AvailableKilobytes * 1024;
}

/// <summary>Parses <c>df -k</c> output into rows with a mount point and 1K block figures.</summary>
internal static class DfOutput
{
    /// <summary>The mount point storage queries prefer when it appears.</summary>
    internal const string DataMountPoint = "/data";

    /// <summary>Parses every well-formed data row, ignoring headers, blank lines, and bad rows.</summary>
    internal static IReadOnlyList<DfRow> Parse(string text)
    {
        var rows = new List<DfRow>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return rows;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 ||
                !long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalKilobytes) ||
                !long.TryParse(columns[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var availableKilobytes) ||
                totalKilobytes < 0 ||
                availableKilobytes < 0)
            {
                continue;
            }

            // Some df variants omit the mount column; the single-mount storage
            // query can still use such a row, while metadata needs the mount.
            var mountPoint = columns.Length >= 6 ? columns[^1] : null;
            rows.Add(new DfRow(mountPoint, totalKilobytes, availableKilobytes));
        }

        return rows;
    }

    /// <summary>Returns the row for an exact mount point, or <see langword="null"/> when absent.</summary>
    internal static DfRow? FindMountPoint(IReadOnlyList<DfRow> rows, string mountPoint)
    {
        foreach (var row in rows)
        {
            if (string.Equals(row.MountPoint, mountPoint, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>Returns the last parsed row, or <see langword="null"/> when there are no rows.</summary>
    internal static DfRow? FindLast(IReadOnlyList<DfRow> rows) =>
        rows.Count == 0 ? null : rows[^1];
}
