using DroidDeck.Adb.Toolbox;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;

namespace DroidDeck.Adb.Tests.Toolbox;

public sealed class AdbDeviceFileServiceTests
{
    private static AdbDeviceFileService Create(FakeProcessRunner runner) =>
        new(runner, () => "adb");

    [Fact]
    public async Task DeleteAsync_WhenConfirmed_HoldsTheDeviceLock()
    {
        using var coordinator = new DeviceOperationCoordinator();
        var observed = false;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                observed = coordinator.ActiveOperations.Any(operation =>
                    string.Equals(operation.Serial, "serial", StringComparison.Ordinal) &&
                    operation.Operation.Contains("Delete", StringComparison.Ordinal));
                return FakeProcessRunner.Success(request);
            },
        };
        var service = new AdbDeviceFileService(runner, () => "adb", coordinator: coordinator);

        var result = await service.DeleteAsync("serial", "/sdcard/file.txt", isDirectory: false, confirmed: true);

        Assert.True(result.Succeeded);
        Assert.True(observed);
        Assert.Empty(coordinator.ActiveOperations);
    }

    [Fact]
    public async Task ListAsync_ParsesEntriesSortsFoldersFirstAndCombinesPaths()
    {
        const string listing = """
            total 24
            -rw-r--r-- 1 shell shell 128 2025-01-02 03:04 app.log
            drwxrwx--x 4 shell shell 4096 2025-01-01 10:00 data
            lrwxrwxrwx 1 root root 7 2025-01-03 09:00 link -> /sdcard
            """;
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, listing),
        };
        var service = Create(runner);

        var entries = await service.ListAsync("serial", "/sdcard/");

        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsDirectory);
        Assert.Equal("/sdcard/data", entries[0].FullPath);
        Assert.Equal("app.log", entries[1].Name);
        Assert.Equal(128, entries[1].Size);
        Assert.NotNull(entries[1].ModifiedAt);
        Assert.Equal("link", entries[2].Name);
        Assert.Equal("/sdcard/link", entries[2].FullPath);
        var request = Assert.Single(runner.Requests);
        Assert.Contains("ls", request.Arguments);
        Assert.Contains("-la", request.Arguments);
    }

    [Fact]
    public async Task PushAsync_RequiresALocalPathAndUsesTheAbsolutePath()
    {
        using var temp = TempFile.Create("payload");
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var result = await service.PushAsync("serial", temp.Path, "/sdcard/Download/payload");
        var missing = await service.PushAsync("serial", temp.Path + ".missing", "/sdcard/x");

        Assert.True(result.Succeeded);
        Assert.False(missing.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Contains("push", request.Arguments);
        Assert.Contains(Path.GetFullPath(temp.Path), request.Arguments);
        Assert.Contains("/sdcard/Download/payload", request.Arguments);
    }

    [Fact]
    public async Task PullAsync_ReportsProgressAndUsesTheDestination()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                return FakeProcessRunner.Success(request);
            },
        };
        var service = Create(runner);
        var progress = new CollectingProgress<FileTransferProgress>();

        var result = await service.PullAsync("serial", "/sdcard/app.log", Path.GetTempPath(), progress);

        Assert.True(result.Succeeded);
        Assert.Contains(progress.Reports, report => report.Phase == "Complete" && report.Percent == 100);
        var request = Assert.Single(runner.Requests);
        Assert.Contains("pull", request.Arguments);
    }

    [Fact]
    public async Task PullAsync_ReportsPercentProgressFromAdbOutput()
    {
        var runner = new FakeProcessRunner
        {
            OutputLines = _ =>
            [
                new ProcessOutputLine(
                    ProcessOutputChannel.StandardOutput,
                    "[ 42%] /sdcard/app.log",
                    DateTimeOffset.UtcNow),
            ],
        };
        var service = Create(runner);
        var progress = new CollectingProgress<FileTransferProgress>();

        await service.PullAsync("serial", "/sdcard/app.log", Path.GetTempPath(), progress);

        Assert.Contains(progress.Reports, report => report.Percent == 42);
        var request = Assert.Single(runner.Requests);
        Assert.Contains("-p", request.Arguments);
        Assert.True(request.SplitOnCarriageReturn);
    }

    [Fact]
    public async Task DeleteAsync_BlocksRootsAndRequiresConfirmation()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var root = await service.DeleteAsync("serial", "/", isDirectory: true, confirmed: true);
        var blocked = await service.DeleteAsync("serial", "/sdcard/app.log", isDirectory: false, confirmed: false);
        var confirmed = await service.DeleteAsync("serial", "/sdcard/app.log", isDirectory: false, confirmed: true);

        Assert.False(root.Succeeded);
        Assert.True(blocked.Blocked);
        Assert.True(blocked.IsDestructive);
        Assert.Contains("rm -f", blocked.CommandLine);
        Assert.Single(runner.Requests);
        Assert.True(confirmed.Succeeded);
    }

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];

        public void Report(T value) => Reports.Add(value);
    }

    private sealed class TempFile : IDisposable
    {
        private TempFile(string path) => Path = path;

        public string Path { get; }

        public static TempFile Create(string name)
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "droiddeck-file-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, name);
            File.WriteAllText(path, "payload");
            return new TempFile(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
