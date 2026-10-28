using DroidDeck.Adb.Toolbox;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Toolbox;

namespace DroidDeck.Adb.Tests.Toolbox;

public sealed class AdbIntentServiceTests
{
    private static AdbIntentService Create(FakeProcessRunner runner) =>
        new(runner, () => "adb");

    [Fact]
    public async Task RebootAsync_WhenConfirmed_HoldsTheDeviceLock()
    {
        using var coordinator = new DeviceOperationCoordinator();
        var observed = false;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                observed = coordinator.ActiveOperations.Any(operation =>
                    string.Equals(operation.Serial, "serial", StringComparison.Ordinal) &&
                    operation.Operation.Contains("Reboot", StringComparison.Ordinal));
                return FakeProcessRunner.Success(request);
            },
        };
        var service = new AdbIntentService(runner, () => "adb", coordinator: coordinator);

        var result = await service.RebootAsync("serial", RebootMode.System, confirmed: true);

        Assert.True(result.Succeeded);
        Assert.True(observed);
        Assert.Empty(coordinator.ActiveOperations);
    }

    [Fact]
    public async Task SendIntentAsync_BuildsActionDataCategoriesAndTypedExtras()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(
                request,
                "Status: ok\nActivity: com.example/.Main\nTotalTime: 42\n"),
        };
        var service = Create(runner);
        var request = new DeviceIntentRequest
        {
            Action = "android.intent.action.VIEW",
            Data = "https://example.com",
            Component = "com.example/.Main",
            Categories = ["android.intent.category.BROWSABLE"],
            Extras =
            [
                new IntentExtra("text", IntentExtraType.Text, "hello"),
                new IntentExtra("count", IntentExtraType.WholeNumber, "3"),
                new IntentExtra("big", IntentExtraType.BigNumber, "9000000000"),
                new IntentExtra("enabled", IntentExtraType.Boolean, "true"),
                new IntentExtra("ratio", IntentExtraType.Fraction, "0.5"),
            ],
        };

        var result = await service.SendIntentAsync("serial", request);

        Assert.True(result.Succeeded);
        Assert.Contains("am start -W", result.CommandLine);
        Assert.Contains("-a android.intent.action.VIEW", result.CommandLine);
        Assert.Contains("-d https://example.com", result.CommandLine);
        Assert.Contains("-n com.example/.Main", result.CommandLine);
        Assert.Contains("-c android.intent.category.BROWSABLE", result.CommandLine);
        Assert.Contains("--es text hello", result.CommandLine);
        Assert.Contains("--ei count 3", result.CommandLine);
        Assert.Contains("--el big 9000000000", result.CommandLine);
        Assert.Contains("--ez enabled true", result.CommandLine);
        Assert.Contains("--ef ratio 0.5", result.CommandLine);
        Assert.Contains("TotalTime", result.Output);
    }

    [Fact]
    public async Task SendIntentAsync_ReportsMalformedExtrasWithoutRunning()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var result = await service.SendIntentAsync(
            "serial",
            new DeviceIntentRequest
            {
                Action = "android.intent.action.VIEW",
                Extras = [new IntentExtra("count", IntentExtraType.WholeNumber, "not-a-number")],
            });

        Assert.False(result.Succeeded);
        Assert.Contains("not a valid", result.Message);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task SendBroadcastAsync_UsesTheActionAndExtras()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, "Broadcast completed: result=0"),
        };
        var service = Create(runner);

        var result = await service.SendBroadcastAsync(
            "serial",
            new DeviceBroadcastRequest
            {
                Action = "com.example.REFRESH",
                Package = "com.example.app",
                Extras = [new IntentExtra("force", IntentExtraType.Boolean, "false")],
            });

        Assert.True(result.Succeeded);
        Assert.Contains("am broadcast -a com.example.REFRESH", result.CommandLine);
        Assert.Contains("-p com.example.app", result.CommandLine);
        Assert.Contains("--ez force false", result.CommandLine);
        Assert.Contains("--include-stopped-packages", result.CommandLine);
    }

    [Fact]
    public async Task RebootAsync_RequiresConfirmationThatNamesTheMode()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var blocked = await service.RebootAsync("serial", RebootMode.Recovery, confirmed: false);
        var confirmed = await service.RebootAsync("serial", RebootMode.Bootloader, confirmed: true);

        Assert.True(blocked.Blocked);
        Assert.Contains("recovery", blocked.Message);
        Assert.Contains("reboot recovery", blocked.CommandLine);
        Assert.True(confirmed.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(["reboot", "bootloader"], request.Arguments.Skip(2));
    }

    [Fact]
    public void ToolEntries_NavigateToPerformanceAndInspector()
    {
        var service = Create(new FakeProcessRunner());

        Assert.Contains(service.ToolEntries, entry =>
            entry.Kind == DeviceToolKind.Performance && entry.NavigationKey == "devices/performance");
        Assert.Contains(service.ToolEntries, entry =>
            entry.Kind == DeviceToolKind.Inspector && entry.NavigationKey == "devices/inspector");
    }
}
