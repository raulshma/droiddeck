using DroidDeck.Adb.Commands;
using DroidDeck.Core.Processes;

namespace DroidDeck.Adb.Tests.Commands;

public sealed class AdbCommandExecutorTests
{
    [Fact]
    public async Task RunForDeviceAsync_PrependsSerialAndForwardsOptions()
    {
        var runner = new FakeProcessRunner();
        var executor = new AdbCommandExecutor(runner, @"C:\sdk\adb.exe");

        var result = await executor.RunForDeviceAsync(
            "S1",
            ["shell", "getprop", "ro.product.model"],
            TimeSpan.FromSeconds(7),
            new AdbCommandOptions
            {
                MaxOutputCharacters = 123,
                SplitOnCarriageReturn = true,
                UseJobObject = false,
            },
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AdbFailureKind.None, result.FailureKind);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(@"C:\sdk\adb.exe", request.FileName);
        Assert.Equal(["-s", "S1", "shell", "getprop", "ro.product.model"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(7), request.Timeout);
        Assert.Equal(123, request.MaxOutputCharacters);
        Assert.True(request.SplitOnCarriageReturn);
        Assert.False(request.UseJobObject);
    }

    [Fact]
    public async Task RunAsync_HostScopedCommandDoesNotAddASerial()
    {
        var runner = new FakeProcessRunner();
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["devices", "-l"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["devices", "-l"], Assert.Single(runner.Requests).Arguments);
    }

    [Fact]
    public async Task DeviceListRowsWithStateNamesAreNotTreatedAsFailures()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(
                request,
                "List of devices attached\nS1 unauthorized\nS2 offline\n"),
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["devices"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("List of devices attached\nS1 unauthorized\nS2 offline\n", result.StandardOutput);
    }

    [Fact]
    public async Task Classify_ReportsUnauthorizedFromStderr()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                ExitCode = 1,
                StandardError = "error: device unauthorized",
            },
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AdbFailureKind.Unauthorized, result.FailureKind);
        Assert.Contains("unauthorized", result.FailureMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("no devices/emulators found", AdbFailureKind.NoDevices)]
    [InlineData("error: device 'S1' not found", AdbFailureKind.DeviceNotFound)]
    [InlineData("error: device offline", AdbFailureKind.Offline)]
    [InlineData("failed to connect to '192.168.1.2:5555': connection refused", AdbFailureKind.ConnectionFailure)]
    [InlineData("Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]", AdbFailureKind.AdbError)]
    [InlineData("error: closed", AdbFailureKind.AdbError)]
    public async Task Classify_RecognizesAdbSignals(string output, AdbFailureKind expected)
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Failure(request, output, exitCode: 0),
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.FailureKind);
    }

    [Fact]
    public async Task Classify_ReportsTimeoutAndCancellation()
    {
        var timedOut = new FakeProcessRunner
        {
            Handler = request => new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                TimedOut = true,
            },
        };
        var canceled = new FakeProcessRunner
        {
            Handler = request => new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                WasCanceled = true,
            },
        };

        var timeoutResult = await new AdbCommandExecutor(timedOut, "adb").RunAsync(
            ["wait"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        var canceledResult = await new AdbCommandExecutor(canceled, "adb").RunAsync(
            ["wait"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(AdbFailureKind.TimedOut, timeoutResult.FailureKind);
        Assert.Equal("adb timed out.", timeoutResult.FailureMessage);
        Assert.Equal(AdbFailureKind.Canceled, canceledResult.FailureKind);
        Assert.Equal("adb was canceled.", canceledResult.FailureMessage);
    }

    [Fact]
    public async Task Classify_ReportsStartErrors()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                StartError = "The system cannot find the file specified.",
            },
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["devices"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AdbFailureKind.StartError, result.FailureKind);
        Assert.Equal(
            "adb could not start: The system cannot find the file specified.",
            result.FailureMessage);
    }

    [Fact]
    public async Task Classify_ReportsPlainExitCodesWithTheCode()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                ExitCode = 3,
                StandardError = "something went wrong",
            },
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AdbFailureKind.ExitCode, result.FailureKind);
        Assert.Equal("adb exited with code 3: something went wrong", result.FailureMessage);
    }

    [Fact]
    public async Task Classify_ReportsNonZeroExitWithoutStderr()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => new ProcessResult
            {
                CommandLine = request.ToCommandLine(),
                ExitCode = 2,
            },
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AdbFailureKind.ExitCode, result.FailureKind);
        Assert.Equal("adb exited with code 2.", result.FailureMessage);
    }

    [Fact]
    public async Task Classify_ReportsSuccessWithoutOutput()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request),
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AdbFailureKind.None, result.FailureKind);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task Classify_ReportsSuccessWithOutput()
    {
        var runner = new FakeProcessRunner
        {
            Handler = request => FakeProcessRunner.Success(request, "ok"),
        };
        var executor = new AdbCommandExecutor(runner, "adb");

        var result = await executor.RunAsync(
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(AdbFailureKind.None, result.FailureKind);
        Assert.Null(result.FailureMessage);
        Assert.Equal("ok", result.StandardOutput);
    }

    [Fact]
    public async Task RunForDeviceAsync_RejectsABlankSerial()
    {
        var executor = new AdbCommandExecutor(new FakeProcessRunner(), "adb");

        await Assert.ThrowsAsync<ArgumentException>(() => executor.RunForDeviceAsync(
            " ",
            ["shell", "true"],
            TimeSpan.FromSeconds(5),
            CancellationToken.None));
    }
}
