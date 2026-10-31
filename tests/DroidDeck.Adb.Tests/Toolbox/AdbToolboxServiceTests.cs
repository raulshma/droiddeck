using DroidDeck.Adb.Toolbox;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Toolbox;

namespace DroidDeck.Adb.Tests.Toolbox;

public sealed class AdbToolboxServiceTests
{
    private static AdbToolboxService Create(FakeProcessRunner runner) =>
        new(runner, () => "adb");

    [Fact]
    public async Task UninstallAppAsync_WhenConfirmed_HoldsTheDeviceLock()
    {
        using var coordinator = new DeviceOperationCoordinator();
        var observed = false;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                observed = coordinator.ActiveOperations.Any(operation =>
                    string.Equals(operation.Serial, "serial", StringComparison.Ordinal) &&
                    operation.Operation.Contains("Uninstall", StringComparison.Ordinal));
                return FakeProcessRunner.Success(request);
            },
        };
        var service = new AdbToolboxService(runner, () => "adb", coordinator: coordinator);

        var result = await service.UninstallAppAsync("serial", "com.example.app", confirmed: true);

        Assert.True(result.Succeeded);
        Assert.True(observed);
        Assert.Empty(coordinator.ActiveOperations);
    }

    [Fact]
    public async Task ListAppsAsync_UsesTheUserFilterAndSortsPackages()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(
                request,
                "package:com.example.zeta\npackage:com.example.alpha\nignored\n"),
        };
        var service = Create(runner);

        var apps = await service.ListAppsAsync("serial", AppListFilter.User);

        Assert.Equal(["com.example.alpha", "com.example.zeta"], apps.Select(app => app.PackageName));
        var request = Assert.Single(runner.Requests);
        Assert.Contains("pm", request.Arguments);
        Assert.Contains("list", request.Arguments);
        Assert.Contains("-3", request.Arguments);
    }

    [Fact]
    public async Task ListAppsAsync_AllCombinesUserAndSystemPackages()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => request.Arguments[^1] == "-s"
                ? FakeProcessRunner.Success(request, "package:com.android.settings\n")
                : FakeProcessRunner.Success(request, "package:com.example.app\n"),
        };
        var service = Create(runner);

        var apps = await service.ListAppsAsync("serial", AppListFilter.All);

        Assert.Equal(["com.android.settings", "com.example.app"], apps.Select(app => app.PackageName));
        Assert.True(apps.Single(app => app.PackageName == "com.android.settings").IsSystem);
        Assert.False(apps.Single(app => app.PackageName == "com.example.app").IsSystem);
        Assert.Equal(2, runner.Requests.Count);
        Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("-u"));
    }

    [Fact]
    public async Task RunShellAsync_ReportsAnOutputFailureEvenWhenAdbExitsZero()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, "error: closed\n"),
        };
        var service = Create(runner);

        var result = await service.RunShellAsync("serial", "input keyevent 3");

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task UninstallWithoutConfirmation_IsBlockedAndShowsTheCommand()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var result = await service.UninstallAppAsync("serial", "com.example.app", confirmed: false);

        Assert.False(result.Succeeded);
        Assert.True(result.Blocked);
        Assert.True(result.IsDestructive);
        Assert.Contains("uninstall", result.CommandLine);
        Assert.Contains("com.example.app", result.CommandLine);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task ConfirmedAppActions_RunTheExactAdbCommand()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var clear = await service.ClearAppDataAsync("serial", "com.example.app", confirmed: true);
        var stop = await service.ForceStopAppAsync("serial", "com.example.app", confirmed: true);

        Assert.True(clear.Succeeded);
        Assert.True(stop.Succeeded);
        Assert.Contains("pm clear com.example.app", clear.CommandLine);
        Assert.Contains("am force-stop com.example.app", stop.CommandLine);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task ListPermissionsAsync_ParsesRuntimeAndInstallSections()
    {
        const string dump = """
                runtime permissions:
                  android.permission.CAMERA: granted=true, flags=[ USER_SENSITIVE_WHEN_GRANTED ]
                  android.permission.POST_NOTIFICATIONS: granted=false, flags=[ ]
                install permissions:
                  android.permission.INTERNET: granted=true
                other section:
                  android.permission.IGNORED: granted=true
            """;
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, dump),
        };
        var service = Create(runner);

        var permissions = await service.ListPermissionsAsync("serial", "com.example.app");

        Assert.Equal(3, permissions.Count);
        Assert.True(permissions.Single(permission => permission.Name == "android.permission.CAMERA").IsRuntime);
        Assert.True(permissions.Single(permission => permission.Name == "android.permission.POST_NOTIFICATIONS").IsRuntime);
        Assert.False(permissions.Single(permission => permission.Name == "android.permission.POST_NOTIFICATIONS").IsGranted);
        Assert.False(permissions.Single(permission => permission.Name == "android.permission.INTERNET").IsRuntime);
        Assert.DoesNotContain(permissions, permission => permission.Name.Contains("IGNORED", StringComparison.Ordinal));
    }

    [Fact]
    public void ParsePermissions_TreatsCrLfAsASingleLineBreak()
    {
        // Built with escapes so the input stays CRLF regardless of the checkout's
        // line endings; a fresh Windows checkout rewrites raw string literals to CRLF.
        const string dump =
            "runtime permissions:\r\n" +
            "  android.permission.CAMERA: granted=true, flags=[ ]\r\n" +
            "install permissions:\r\n" +
            "  android.permission.INTERNET: granted=true\r\n";

        var permissions = AdbToolboxService.ParsePermissions(dump);

        Assert.Equal(2, permissions.Count);
        Assert.True(permissions.Single(permission => permission.Name == "android.permission.CAMERA").IsRuntime);
        Assert.False(permissions.Single(permission => permission.Name == "android.permission.INTERNET").IsRuntime);
    }

    [Fact]
    public async Task SetPermissionAsync_GrantsAndReportsFailures()
    {
        var calls = 0;
        var runner = new FakeProcessRunner
        {
            Handler = request =>
            {
                calls++;
                return calls == 1
                    ? FakeProcessRunner.Success(request)
                    : FakeProcessRunner.Failure(request, "Error: permission not found");
            },
        };
        var service = Create(runner);

        var granted = await service.SetPermissionAsync(
            "serial",
            "com.example.app",
            "android.permission.CAMERA",
            grant: true);
        var revoked = await service.SetPermissionAsync(
            "serial",
            "com.example.app",
            "android.permission.NOPE",
            grant: false);

        Assert.True(granted.Succeeded);
        Assert.Contains("pm grant com.example.app android.permission.CAMERA", granted.CommandLine);
        Assert.False(revoked.Succeeded);
        Assert.Contains("permission not found", revoked.Message);
        Assert.Contains("pm revoke com.example.app android.permission.NOPE", revoked.CommandLine);
    }

    [Fact]
    public async Task RunShellAsync_TokenizesArgumentsAndRecordsNewestHistoryFirst()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, "value"),
        };
        var service = Create(runner);

        var first = await service.RunShellAsync("serial", "getprop ro.product.model");
        var second = await service.RunShellAsync("serial", "echo \"hello world\"");

        Assert.True(first.Succeeded);
        Assert.Equal(["shell", "getprop", "ro.product.model"], runner.Requests[0].Arguments.Skip(2));
        Assert.Equal(["shell", "echo", "hello world"], runner.Requests[1].Arguments.Skip(2));
        Assert.Equal(2, service.ShellHistory.Count);
        Assert.Equal("echo \"hello world\"", service.ShellHistory[0].Command);
        Assert.Equal("getprop ro.product.model", service.ShellHistory[1].Command);

        service.ClearShellHistory();
        Assert.Empty(service.ShellHistory);
    }

    [Fact]
    public async Task RunShellAsync_BlocksDestructiveCommandsUntilConfirmed()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var blocked = await service.RunShellAsync("serial", "pm clear com.example.app");
        var allowed = await service.RunShellAsync("serial", "pm clear com.example.app", allowDestructive: true);

        Assert.True(blocked.Blocked);
        Assert.True(blocked.IsDestructive);
        Assert.Contains("pm clear", blocked.CommandLine);
        Assert.True(allowed.Succeeded);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task RunShellAsync_BlocksPipelineMetacharacters()
    {
        var runner = new FakeProcessRunner();
        var service = Create(runner);

        var result = await service.RunShellAsync("serial", "ls | grep data");

        Assert.True(result.Blocked);
        Assert.Contains("metacharacters", result.Message);
        Assert.Empty(runner.Requests);
        Assert.Empty(service.ShellHistory);
    }

    [Fact]
    public async Task PortManagement_ListsAddsAndRemovesMappings()
    {
        var responses = new Queue<string>([
            "serial tcp:8080 tcp:9090\n",
            "serial tcp:4000 tcp:5000\n",
            string.Empty,
            string.Empty,
            string.Empty,
        ]);
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, responses.Dequeue()),
        };
        var service = Create(runner);

        var forwards = await service.ListPortsAsync("serial", PortDirection.Forward);
        var reverses = await service.ListPortsAsync("serial", PortDirection.Reverse);
        var added = await service.AddPortAsync("serial", PortDirection.Forward, "tcp:8080", "tcp:9090");
        var removed = await service.RemovePortAsync("serial", PortDirection.Reverse, "tcp:4000", "tcp:5000");
        var removedAll = await service.RemoveAllPortsAsync("serial", PortDirection.Forward, confirmed: true);

        var forward = Assert.Single(forwards);
        Assert.Equal("tcp:8080", forward.Local);
        Assert.Equal("tcp:9090", forward.Remote);
        var reverse = Assert.Single(reverses);
        Assert.Equal("tcp:4000", reverse.Local);
        Assert.Equal("tcp:5000", reverse.Remote);
        Assert.True(added.Succeeded);
        Assert.Contains("forward tcp:8080 tcp:9090", added.CommandLine);
        Assert.True(removed.Succeeded);
        Assert.Contains("reverse --remove tcp:4000", removed.CommandLine);
        Assert.True(removedAll.Succeeded);
        Assert.Contains("forward --remove-all", removedAll.CommandLine);
    }
}
