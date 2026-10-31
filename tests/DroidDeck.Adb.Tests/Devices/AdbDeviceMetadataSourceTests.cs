using DroidDeck.Adb.Devices;
using DroidDeck.Core.Processes;

namespace DroidDeck.Adb.Tests.Devices;

public sealed class AdbDeviceMetadataSourceTests
{
    private const string FullOutput =
        """
        ---PROP---
        [ro.product.manufacturer]: [Google]
        [ro.product.model]: [Pixel 7]
        [ro.product.name]: [panther]
        [ro.build.version.release]: [14]
        [ro.build.version.sdk]: [34]
        [ro.product.cpu.abi]: [arm64-v8a]
        [ro.product.cpu.abilist]: [arm64-v8a,armeabi-v7a]
        [ro.build.type]: [userdebug]
        [ro.debuggable]: [1]
        [ro.build.characteristics]: [nosdcard]
        [ro.kernel.qemu]: [0]
        ---WM---
        Physical size: 1080x2400
        Physical density: 420
        ---BAT---
        Current Battery Service state:
          AC powered: true
          level: 73
          status: 2
        ---NET---
        2: wlan0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1500 qdisc mq state UP group default qlen 3000
            inet 192.168.1.42/24 brd 192.168.1.255 scope global wlan0
        ---DF---
        Filesystem     1K-blocks     Used Available Use% Mounted on
        /dev/block/dm-11 115457024 56789012 58668012 50% /data
        """;

    [Fact]
    public async Task FetchAsync_UsesOneSectionedShellInvocation()
    {
        var runner = new StubProcessRunner { Result = Success(FullOutput) };
        var source = new AdbDeviceMetadataSource(runner);

        _ = await source.FetchAsync("S1");

        var request = Assert.Single(runner.Requests);
        Assert.Equal("adb", request.FileName);
        Assert.Equal(["-s", "S1", "shell", AdbDeviceMetadataSource.BatchScript], request.Arguments);
        Assert.Contains("---PROP---", request.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("dumpsys battery", request.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("df -k /data", request.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_ParsesAllSections()
    {
        var runner = new StubProcessRunner { Result = Success(FullOutput) };
        var source = new AdbDeviceMetadataSource(runner);

        var metadata = await source.FetchAsync("S1");

        Assert.Equal("Google", metadata.Manufacturer);
        Assert.Equal("Pixel 7", metadata.Model);
        Assert.Equal("panther", metadata.Product);
        Assert.Equal("14", metadata.AndroidVersion);
        Assert.Equal(34, metadata.ApiLevel);
        Assert.Equal("arm64-v8a", metadata.Abi);
        Assert.Equal(["arm64-v8a", "armeabi-v7a"], metadata.AbiList);
        Assert.Equal("userdebug", metadata.BuildType);
        Assert.True(metadata.IsDebuggable);
        Assert.False(metadata.IsEmulator);
        Assert.Null(metadata.AvdName);
        Assert.Equal("nosdcard", metadata.Characteristics);
        Assert.Equal("1080x2400", metadata.ScreenSize);
        Assert.Equal(420, metadata.Density);
        Assert.Equal(73, metadata.BatteryLevel);
        Assert.Equal("Charging", metadata.BatteryStatus);
        Assert.Equal("192.168.1.42", metadata.IpAddress);
        Assert.Equal(115457024L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(58668012L * 1024, metadata.StorageFreeBytes);
        Assert.NotEqual(default, metadata.FetchedAt);
        Assert.Empty(metadata.Warnings);
    }

    [Fact]
    public void ParseBatch_ParsesStorageFromFullOutput()
    {
        var metadata = AdbDeviceMetadataSource.ParseBatch(FullOutput, DateTimeOffset.UtcNow);
        Assert.Equal(115457024L * 1024, metadata.StorageTotalBytes);
        Assert.Equal(58668012L * 1024, metadata.StorageFreeBytes);
    }

    [Fact]
    public async Task FetchAsync_ReadsEmulatorProperties()
    {
        var output =
            """
            ---PROP---
            [ro.product.manufacturer]: [Google]
            [ro.product.model]: [sdk_gphone64_x86_64]
            [ro.build.version.release]: [14]
            [ro.build.version.sdk]: [34]
            [ro.product.cpu.abi]: [x86_64]
            [ro.kernel.qemu]: [1]
            [ro.boot.qemu.avd_name]: [Pixel_7_API_34]
            ---WM---
            Physical size: 1080x2400
            Physical density: 420
            ---BAT---
            level: 88
            status: 5
            ---NET---
            ---DF---
            /dev/block/dm-11 1000 250 750 25% /data
            """;
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = Success(output) });

        var metadata = await source.FetchAsync("emulator-5554");

        Assert.True(metadata.IsEmulator);
        Assert.Equal("Pixel_7_API_34", metadata.AvdName);
        Assert.Equal("Full", metadata.BatteryStatus);
    }

    [Fact]
    public async Task FetchAsync_UsesOverridesForScreenSizeAndDensity()
    {
        var output =
            """
            ---PROP---
            [ro.product.manufacturer]: [Google]
            [ro.product.model]: [Pixel]
            [ro.build.version.release]: [13]
            [ro.build.version.sdk]: [33]
            [ro.product.cpu.abi]: [arm64-v8a]
            ---WM---
            Physical size: 1440x3120
            Override size: 1080x2400
            Physical density: 560
            Override density: 420
            ---BAT---
            level: 50
            status: 3
            """;
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = Success(output) });

        var metadata = await source.FetchAsync("S1");

        Assert.Equal("1080x2400", metadata.ScreenSize);
        Assert.Equal(420, metadata.Density);
        Assert.Equal("Discharging", metadata.BatteryStatus);
    }

    [Fact]
    public async Task FetchAsync_ReportsMissingSectionsWithoutFailingTheDevice()
    {
        var output =
            """
            ---PROP---
            [ro.product.manufacturer]: [Google]
            [ro.product.model]: [Pixel 7]
            [ro.build.version.release]: [14]
            [ro.build.version.sdk]: [34]
            [ro.product.cpu.abi]: [arm64-v8a]
            """;
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = Success(output) });

        var metadata = await source.FetchAsync("S1");

        Assert.Equal("Google", metadata.Manufacturer);
        Assert.Equal("Pixel 7", metadata.Model);
        Assert.Null(metadata.ScreenSize);
        Assert.Null(metadata.BatteryLevel);
        Assert.Null(metadata.IpAddress);
        Assert.Null(metadata.StorageTotalBytes);
        Assert.Contains(metadata.Warnings, static warning => warning.Contains("Screen information", StringComparison.Ordinal));
        Assert.Contains(metadata.Warnings, static warning => warning.Contains("Battery information", StringComparison.Ordinal));
        Assert.Contains(metadata.Warnings, static warning => warning.Contains("Network information", StringComparison.Ordinal));
        Assert.Contains(metadata.Warnings, static warning => warning.Contains("Storage information", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_KeepsParsedFieldsWhenAdbExitsWithAnError()
    {
        var result = Success(FullOutput) with
        {
            ExitCode = 1,
            StandardError = "error: closed\nsecond line",
        };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        Assert.Equal("Pixel 7", metadata.Model);
        Assert.Equal(73, metadata.BatteryLevel);
        Assert.Contains(metadata.Warnings, static warning => warning.Contains("error: closed", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata.Warnings, static warning => warning.Contains("second line", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchAsync_ReturnsWarningWhenAdbCannotStart()
    {
        var result = Success(string.Empty) with { ExitCode = -1, StartError = "The system cannot find the file specified." };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        Assert.Null(metadata.Model);
        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("adb could not start: The system cannot find the file specified.", warning);
    }

    [Fact]
    public async Task FetchAsync_ReturnsWarningOnTimeout()
    {
        var result = Success(string.Empty) with { TimedOut = true, ExitCode = -1 };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("Metadata collection timed out.", warning);
    }

    [Fact]
    public async Task FetchAsync_ReturnsWarningOnCancellation()
    {
        var result = Success(string.Empty) with { WasCanceled = true, ExitCode = -1 };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("Metadata collection was canceled.", warning);
    }

    [Fact]
    public async Task FetchAsync_ReturnsWarningWhenOutputIsEmpty()
    {
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = Success("  ") });

        var metadata = await source.FetchAsync("S1");

        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("Metadata collection returned no output.", warning);
    }

    [Fact]
    public async Task FetchAsync_ReportsExitCodeAndStderrWhenOutputIsMissing()
    {
        var result = Success("  ") with
        {
            ExitCode = 2,
            StandardError = "something went wrong\nsecond line",
        };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("Metadata collection failed: something went wrong (exit code 2)", warning);
    }

    [Fact]
    public async Task FetchAsync_ReportsExitCodeWhenOutputAndStderrAreMissing()
    {
        var result = Success(string.Empty) with { ExitCode = 2 };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("Metadata collection returned no output. (exit code 2)", warning);
    }

    [Fact]
    public async Task FetchAsync_UsesClassifierSignalWhenOutputIsMissing()
    {
        var result = Success(string.Empty) with { StandardError = "error: closed" };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        var warning = Assert.Single(metadata.Warnings);
        Assert.Equal("Metadata collection failed: error: closed", warning);
    }

    [Fact]
    public async Task FetchAsync_AppendsExitCodeWhenAdbWritesNoStderr()
    {
        var result = Success(FullOutput) with { ExitCode = 1 };
        var source = new AdbDeviceMetadataSource(new StubProcessRunner { Result = result });

        var metadata = await source.FetchAsync("S1");

        Assert.Equal("Pixel 7", metadata.Model);
        Assert.Contains(
            metadata.Warnings,
            static warning => warning == "adb reported an error while reading metadata: exit code 1");
    }

    [Fact]
    public async Task FetchAsync_RejectsBlankSerial()
    {
        var source = new AdbDeviceMetadataSource(new StubProcessRunner());
        await Assert.ThrowsAnyAsync<ArgumentException>(() => source.FetchAsync("  "));
    }

    [Theory]
    [InlineData("1", "Unknown")]
    [InlineData("2", "Charging")]
    [InlineData("3", "Discharging")]
    [InlineData("4", "Not charging")]
    [InlineData("5", "Full")]
    public void ParseBatch_MapsBatteryStatuses(string status, string expected)
    {
        var output = $"""
            ---PROP---
            ---BAT---
            level: 42
            status: {status}
            """;

        var metadata = AdbDeviceMetadataSource.ParseBatch(output, DateTimeOffset.UtcNow);

        Assert.Equal(expected, metadata.BatteryStatus);
        Assert.Equal(42, metadata.BatteryLevel);
    }

    [Fact]
    public void ParseBatch_IgnoresMalformedProperties()
    {
        var output =
            """
            ---PROP---
            not a property line
            [ro.product.model]: [Pixel]
            [broken
            [empty]: []
            ---BAT---
            level: not-a-number
            """;

        var metadata = AdbDeviceMetadataSource.ParseBatch(output, DateTimeOffset.UtcNow);

        Assert.Equal("Pixel", metadata.Model);
        Assert.Null(metadata.BatteryLevel);
    }

    private static ProcessResult Success(string output) => new()
    {
        CommandLine = "adb",
        ExitCode = 0,
        StandardOutput = output,
    };

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly object _gate = new();
        private readonly List<ProcessRequest> _requests = [];

        public ProcessResult Result { get; set; } = new() { CommandLine = "adb", ExitCode = 0 };

        public IReadOnlyList<ProcessRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _requests.Add(request);
            }

            return Task.FromResult(Result);
        }
    }
}
