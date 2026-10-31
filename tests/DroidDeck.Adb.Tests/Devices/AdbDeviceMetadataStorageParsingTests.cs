using DroidDeck.Adb.Devices;
using DroidDeck.Core.Devices;

namespace DroidDeck.Adb.Tests.Devices;

/// <summary>
/// Exercises the shared <c>df -k</c> parser through the public metadata surface, including
/// header handling, mount selection, malformed rows, and line-ending tolerance.
/// </summary>
public sealed class AdbDeviceMetadataStorageParsingTests
{
    private const string Header = "Filesystem     1K-blocks     Used Available Use% Mounted on";

    [Fact]
    public void ParseBatch_ReadsDataRowUnderHeader()
    {
        var metadata = ParseStorage(
            $"""
            {Header}
            /dev/block/dm-11 115457024 56789012 58668012 50% /data
            """);

        Assert.Equal(115457024L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(58668012L * 1024, metadata.StorageFreeBytes);
        Assert.DoesNotContain(metadata.Warnings, static warning => warning.Contains("Storage", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseBatch_SelectsDataMountFromMultipleRows()
    {
        var metadata = ParseStorage(
            $"""
            {Header}
            /dev/block/dm-11 200 50 150 25% /
            /dev/block/dm-12 900 300 600 33% /data
            /dev/block/dm-13 400 100 300 25% /storage/emulated
            """);

        Assert.Equal(900L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(600L * 1024, metadata.StorageFreeBytes);
    }

    [Fact]
    public void ParseBatch_SelectsFirstDataMountWhenRepeated()
    {
        var metadata = ParseStorage(
            $"""
            {Header}
            /dev/block/dm-11 100 20 80 20% /data
            /dev/block/dm-12 900 300 600 33% /data
            """);

        Assert.Equal(100L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(80L * 1024, metadata.StorageFreeBytes);
    }

    [Fact]
    public void ParseBatch_ReportsMissingDataMount()
    {
        var metadata = ParseStorage(
            $"""
            {Header}
            /dev/block/dm-11 200 50 150 25% /
            /dev/block/dm-13 400 100 300 25% /storage/emulated
            """);

        Assert.Null(metadata.StorageTotalBytes);
        Assert.Null(metadata.StorageFreeBytes);
        Assert.Contains(
            metadata.Warnings,
            static warning => warning.Contains("Storage information could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseBatch_DoesNotMatchDataMountWithTrailingSlash()
    {
        // The existing metadata contract requires an exact /data mount point.
        var metadata = ParseStorage(
            $"""
            {Header}
            /dev/block/dm-11 100 20 80 20% /data/
            """);

        Assert.Null(metadata.StorageTotalBytes);
        Assert.Null(metadata.StorageFreeBytes);
    }

    [Theory]
    [InlineData("Filesystem 1K-blocks Used Available\n/dev/block/dm-11 100 20")]
    [InlineData("Filesystem 1K-blocks Used Available\n/dev/block/dm-11 100 20 80")]
    [InlineData("Filesystem 1K-blocks Used Available Use%\n/dev/block/dm-11 100 20 80 20%")]
    [InlineData("/dev/block/dm-11 100 20 80 /data")]
    public void ParseBatch_IgnoresRowsWithoutAnExplicitMountColumn(string dfOutput)
    {
        var metadata = ParseStorage(dfOutput);

        Assert.Null(metadata.StorageTotalBytes);
        Assert.Null(metadata.StorageFreeBytes);
    }

    [Theory]
    [InlineData("/dev/block/dm-11 not-a-number 20 80 20% /data")]
    [InlineData("/dev/block/dm-11 100 20 not-a-number 20% /data")]
    [InlineData("/dev/block/dm-11 -100 20 80 20% /data")]
    [InlineData("/dev/block/dm-11 100 20 -80 20% /data")]
    public void ParseBatch_IgnoresMalformedOrNegativeRows(string dataRow)
    {
        var metadata = ParseStorage($"{Header}\n{dataRow}");

        Assert.Null(metadata.StorageTotalBytes);
        Assert.Null(metadata.StorageFreeBytes);
    }

    [Fact]
    public void ParseBatch_IgnoresLeadingAndTrailingBlankLines()
    {
        var metadata = ParseStorage(
            $"""


            {Header}
            /dev/block/dm-11 100 20 80 20% /data


            """);

        Assert.Equal(100L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(80L * 1024, metadata.StorageFreeBytes);
    }

    [Fact]
    public void ParseBatch_ParsesCrLfOutput()
    {
        var output = string.Join(
            "\r\n",
            Header,
            "/dev/block/dm-11 100 20 80 20% /data",
            string.Empty);

        var metadata = ParseStorage(output);

        Assert.Equal(100L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(80L * 1024, metadata.StorageFreeBytes);
    }

    [Fact]
    public void ParseBatch_ReturnsNoStorageForEmptyOutput()
    {
        var metadata = ParseStorage(string.Empty);

        Assert.Null(metadata.StorageTotalBytes);
        Assert.Null(metadata.StorageFreeBytes);
        Assert.Contains(
            metadata.Warnings,
            static warning => warning.Contains("Storage information could not be read", StringComparison.Ordinal));
    }

    private static DeviceMetadata ParseStorage(string dfOutput) =>
        AdbDeviceMetadataSource.ParseBatch(
            $"---DF---\n{dfOutput}",
            DateTimeOffset.UtcNow);
}
