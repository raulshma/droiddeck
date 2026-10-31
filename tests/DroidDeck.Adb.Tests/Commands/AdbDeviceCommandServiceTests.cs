using DroidDeck.Adb.Commands;

namespace DroidDeck.Adb.Tests.Commands;

public sealed class AdbDeviceCommandServiceTests
{
    private const string Header = "Filesystem     1K-blocks     Used Available Use% Mounted on";

    [Fact]
    public async Task GetFreeStorageAsync_ParsesSingleDataRowOutput()
    {
        var service = CreateService(
            $"""
            {Header}
            /dev/block/dm-11 115457024 56789012 58668012 50% /data
            """);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Equal(58668012L * 1024, free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_SelectsDataMountFromMultipleRows()
    {
        var service = CreateService(
            $"""
            {Header}
            /dev/block/dm-11 200 50 150 25% /
            /dev/block/dm-13 400 100 300 25% /storage/emulated
            /dev/block/dm-12 900 300 600 33% /data
            """);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Equal(600L * 1024, free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_FallsBackToLastRowWithoutDataMount()
    {
        var service = CreateService(
            $"""
            {Header}
            /dev/block/dm-11 200 50 150 25% /
            /dev/block/dm-13 400 100 300 25% /storage/emulated
            """);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Equal(300L * 1024, free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_FallsBackToLastRowForNonExactDataMount()
    {
        var service = CreateService(
            $"""
            {Header}
            /dev/block/dm-11 200 50 150 25% /data/
            """);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Equal(150L * 1024, free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_AcceptsRowsWithoutAMountColumn()
    {
        // Older df builds omit the mount column; the single-mount query still
        // reads the available figure from the last row.
        var service = CreateService(
            $"""
            {Header}
            /dev/block/dm-11 100 20 80
            """);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Equal(80L * 1024, free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_ParsesCrLfOutput()
    {
        var output = string.Join(
            "\r\n",
            Header,
            "/dev/block/dm-11 100 20 80 20% /data",
            string.Empty);
        var service = CreateService(output);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Equal(80L * 1024, free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_ReturnsNullWhenCommandFails()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Failure(
                request,
                $"{Header}\n/dev/block/dm-11 100 20 80 20% /data",
                exitCode: 1),
        };
        var service = new AdbDeviceCommandService(runner, () => "adb");

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Null(free);
    }

    [Theory]
    [InlineData("")]
    [InlineData(Header)]
    [InlineData("Filesystem 1K-blocks Used Available\n/dev/block/dm-11 100 20")]
    [InlineData("/dev/block/dm-11 100 20 -80 20% /data")]
    public async Task GetFreeStorageAsync_ReturnsNullForUnusableOutput(string output)
    {
        var service = CreateService(output);

        var free = await service.GetFreeStorageAsync("S1");

        Assert.Null(free);
    }

    [Fact]
    public async Task GetFreeStorageAsync_RequestsDfForData()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, Header),
        };
        var service = new AdbDeviceCommandService(runner, () => "adb");

        _ = await service.GetFreeStorageAsync("S1");

        var request = Assert.Single(runner.Requests);
        Assert.Equal(["-s", "S1", "shell", "df", "-k", "/data"], request.Arguments);
    }

    private static AdbDeviceCommandService CreateService(string output)
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, output),
        };
        return new AdbDeviceCommandService(runner, () => "adb");
    }
}
