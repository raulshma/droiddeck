using System.ComponentModel;
using DroidDeck.Adb.Emulators;
using DroidDeck.Core.Emulators;
using DroidDeck.Core.Processes;

namespace DroidDeck.Adb.Tests.Emulators;

public sealed class EmulatorServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _avdHome;

    public EmulatorServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "droiddeck-emulator-tests-" + Guid.NewGuid().ToString("N"));
        _avdHome = Path.Combine(_root, "avd");
        Directory.CreateDirectory(_avdHome);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ListAsync_ReportsApiStateSerialAndSnapshots()
    {
        CreateAvd("Pixel_7_API_34", "android-34", "x86_64", "pixel_7", ["quick", "clean"]);
        var runner = new FakeProcessRunner { Handler = StandardHandler("Pixel_7_API_34", booted: true) };
        using var service = CreateService(runner);

        var avds = await service.ListAsync();

        var avd = Assert.Single(avds);
        Assert.Equal("Pixel_7_API_34", avd.Name);
        Assert.Equal(34, avd.ApiLevel);
        Assert.Equal("android-34", avd.Target);
        Assert.Equal("x86_64", avd.Abi);
        Assert.Equal("pixel_7", avd.DeviceProfile);
        Assert.Equal(EmulatorAvdState.Running, avd.State);
        Assert.Equal("emulator-5554", avd.Serial);
        Assert.Equal(["clean", "quick"], avd.Snapshots);
        Assert.Null(service.AvailabilityError);
    }

    [Fact]
    public async Task LaunchAsync_StartsDetachedProcessUsesColdBootAndWaitsForBoot()
    {
        CreateAvd("Pixel_7_API_34", "android-34", "x86_64", "pixel_7", []);
        var devicesCalls = 0;
        var bootChecks = 0;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (IsEmulatorList(request))
                {
                    return FakeProcessRunner.Success(request, "Pixel_7_API_34\n");
                }

                if (IsDevices(request))
                {
                    devicesCalls++;
                    return devicesCalls == 1
                        ? FakeProcessRunner.Success(request, "List of devices attached\n")
                        : FakeProcessRunner.Success(request, "List of devices attached\nemulator-5554\tdevice\n");
                }

                if (IsAvdName(request))
                {
                    return FakeProcessRunner.Success(request, "Pixel_7_API_34\nOK\n");
                }

                if (IsBootCompleted(request))
                {
                    bootChecks++;
                    return FakeProcessRunner.Success(request, bootChecks >= 2 ? "1\n" : "0\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        var launcher = new FakeProcessLauncher();
        using var service = CreateService(runner, launcher, pollInterval: TimeSpan.FromMilliseconds(1));

        var result = await service.LaunchAsync(
            "Pixel_7_API_34",
            new EmulatorLaunchOptions { ColdBoot = true, BootTimeout = TimeSpan.FromSeconds(5) });

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("booted", result.Message, StringComparison.OrdinalIgnoreCase);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal("emulator", request.FileName);
        Assert.Contains("-avd", request.Arguments);
        Assert.Contains("Pixel_7_API_34", request.Arguments);
        Assert.Contains("-no-snapshot-load", request.Arguments);
        Assert.True(bootChecks >= 2);
        Assert.Equal(EmulatorAvdState.Running, Assert.Single(service.Avds).State);
    }

    [Fact]
    public async Task WipeDataAsync_DeletesUserDataAndSnapshots()
    {
        var avdDirectory = CreateAvd("Pixel_7_API_34", "android-34", "x86_64", "pixel_7", ["quick"]);
        var userData = Path.Combine(avdDirectory, "userdata-qemu.img");
        var cache = Path.Combine(avdDirectory, "cache.img");
        await File.WriteAllTextAsync(userData, "data");
        await File.WriteAllTextAsync(cache, "cache");
        var runner = new FakeProcessRunner
        {
            Handler = request => IsEmulatorList(request)
                ? FakeProcessRunner.Success(request, "Pixel_7_API_34\n")
                : IsDevices(request)
                    ? FakeProcessRunner.Success(request, "List of devices attached\n")
                    : FakeProcessRunner.Success(request),
        };
        using var service = CreateService(runner);

        var result = await service.WipeDataAsync("Pixel_7_API_34");

        Assert.True(result.Succeeded, result.Message);
        Assert.False(File.Exists(userData));
        Assert.False(File.Exists(cache));
        Assert.False(Directory.Exists(Path.Combine(avdDirectory, "snapshots")));
    }

    [Fact]
    public async Task WipeDataAsync_RefusesToWipeRunningEmulator()
    {
        var avdDirectory = CreateAvd("Pixel_7_API_34", "android-34", "x86_64", "pixel_7", []);
        var userData = Path.Combine(avdDirectory, "userdata-qemu.img");
        await File.WriteAllTextAsync(userData, "data");
        var runner = new FakeProcessRunner { Handler = StandardHandler("Pixel_7_API_34", booted: true) };
        using var service = CreateService(runner);

        var result = await service.WipeDataAsync("Pixel_7_API_34");

        Assert.False(result.Succeeded);
        Assert.Contains("running", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(userData));
    }

    [Fact]
    public async Task StopAsync_UsesAdbEmuKillAndRefreshesState()
    {
        CreateAvd("Pixel_7_API_34", "android-34", "x86_64", "pixel_7", []);
        var killed = false;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                if (IsEmulatorList(request))
                {
                    return FakeProcessRunner.Success(request, "Pixel_7_API_34\n");
                }

                if (IsDevices(request))
                {
                    return killed
                        ? FakeProcessRunner.Success(request, "List of devices attached\n")
                        : FakeProcessRunner.Success(request, "List of devices attached\nemulator-5554\tdevice\n");
                }

                if (IsAvdName(request))
                {
                    return FakeProcessRunner.Success(request, "Pixel_7_API_34\nOK\n");
                }

                if (IsBootCompleted(request))
                {
                    return FakeProcessRunner.Success(request, "1\n");
                }

                if (FakeProcessRunner.HasArguments(request, "-s", "emulator-5554", "emu", "kill"))
                {
                    killed = true;
                    return FakeProcessRunner.Success(request, "OK\n");
                }

                return FakeProcessRunner.Success(request);
            },
        };
        using var service = CreateService(runner, pollInterval: TimeSpan.FromMilliseconds(1));
        await service.ListAsync();

        var result = await service.StopAsync("Pixel_7_API_34");

        Assert.True(result.Succeeded, result.Message);
        Assert.Contains("emu kill", result.CommandLine ?? string.Empty);
        Assert.Equal(EmulatorAvdState.Stopped, Assert.Single(service.Avds).State);
    }

    [Fact]
    public async Task ListAsync_ReportsActionableGuidanceWhenEmulatorIsMissing()
    {
        var runner = new FakeProcessRunner
        {
            Handler = _ => throw new Win32Exception("The system cannot find the file specified."),
        };
        using var service = CreateService(runner);

        var avds = await service.ListAsync();

        Assert.Empty(avds);
        Assert.NotNull(service.AvailabilityError);
        Assert.Contains("SDK Manager", service.AvailabilityError, StringComparison.OrdinalIgnoreCase);
    }

    private EmulatorService CreateService(
        FakeProcessRunner runner,
        FakeProcessLauncher? launcher = null,
        TimeSpan? pollInterval = null)
    {
        return new EmulatorService(
            runner,
            launcher ?? new FakeProcessLauncher(),
            static () => "emulator",
            static () => "adb",
            () => _avdHome,
            pollInterval ?? TimeSpan.FromMilliseconds(1));
    }

    private static Func<ProcessRequest, ProcessResult> StandardHandler(string avdName, bool booted) => request =>
    {
        if (IsEmulatorList(request))
        {
            return FakeProcessRunner.Success(request, avdName + "\n");
        }

        if (IsDevices(request))
        {
            return FakeProcessRunner.Success(request, "List of devices attached\nemulator-5554\tdevice\n");
        }

        if (IsAvdName(request))
        {
            return FakeProcessRunner.Success(request, avdName + "\nOK\n");
        }

        if (IsBootCompleted(request))
        {
            return FakeProcessRunner.Success(request, booted ? "1\n" : "0\n");
        }

        return FakeProcessRunner.Success(request);
    };

    private string CreateAvd(
        string name,
        string target,
        string abi,
        string device,
        IReadOnlyList<string> snapshots)
    {
        var directory = Path.Combine(_avdHome, name + ".avd");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "config.ini"),
            $"target={target}{Environment.NewLine}" +
            $"abi.type={abi}{Environment.NewLine}" +
            $"hw.device.name={device}{Environment.NewLine}" +
            $"image.sysdir.1=system-images;{target};google_apis;{abi}{Environment.NewLine}");
        foreach (var snapshot in snapshots)
        {
            Directory.CreateDirectory(Path.Combine(directory, "snapshots", snapshot));
        }

        return directory;
    }

    private static bool IsEmulatorList(ProcessRequest request) =>
        request.FileName == "emulator" && request.Arguments.SequenceEqual(["-list-avds"]);

    private static bool IsDevices(ProcessRequest request) =>
        request.FileName == "adb" && request.Arguments.SequenceEqual(["devices"]);

    private static bool IsAvdName(ProcessRequest request) =>
        request.FileName == "adb" &&
        request.Arguments.Count == 5 &&
        request.Arguments[0] == "-s" &&
        request.Arguments[2] == "emu" &&
        request.Arguments[3] == "avd" &&
        request.Arguments[4] == "name";

    private static bool IsBootCompleted(ProcessRequest request) =>
        request.FileName == "adb" &&
        request.Arguments.Count == 5 &&
        request.Arguments[2] == "shell" &&
        request.Arguments[3] == "getprop" &&
        request.Arguments[4] == "sys.boot_completed";
}
