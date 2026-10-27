using System.Diagnostics;
using System.Globalization;
using DroidDeck.Core.Processes;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Processes;

public sealed class ProcessRunnerTests
{
    private static readonly ProcessRunner Runner = new();

    [Fact]
    public async Task CapturesStandardOutputAndExitCode()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("echo", "hello"));

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hello", result.StandardOutput.Trim());
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(result.OutputTruncated);
        Assert.False(result.TimedOut);
        Assert.False(result.WasCanceled);
        Assert.Null(result.StartError);
    }

    [Fact]
    public async Task CapturesStandardErrorAndNonZeroExitCode()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("both", "out text", "err text"));

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out text", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("err text", result.StandardError, StringComparison.Ordinal);

        var failing = await Runner.RunAsync(TestProcessHelper.Create("stderr", "failure"));
        Assert.False(failing.Succeeded);
        Assert.Equal(2, failing.ExitCode);
        Assert.Contains("failure", failing.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StripsAnsiEscapeSequences()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("ansi"));

        Assert.Equal("red plain green", result.StandardOutput.Trim());
        Assert.DoesNotContain('\u001b', result.StandardOutput);
    }

    [Fact]
    public async Task PreservesArgumentsWithoutShellInterpolation()
    {
        var argument = "a b\"c";
        var result = await Runner.RunAsync(TestProcessHelper.Create("echo", argument));

        Assert.Equal(argument, result.StandardOutput.Trim());
    }

    [Fact]
    public void FormatsCommandLinesWithQuoting()
    {
        var commandLine = ProcessRequest.FormatCommandLine(
            @"C:\Program Files\Tool\tool.exe",
            ["plain", "with space", "q\"uote", "back\\slash"]);

        Assert.Equal(
            "\"C:\\Program Files\\Tool\\tool.exe\" plain \"with space\" \"q\\\"uote\" \"back\\slash\"",
            commandLine);
    }

    [Fact]
    public async Task ReportsStartFailureForMissingExecutable()
    {
        var result = await Runner.RunAsync(new ProcessRequest
        {
            FileName = Path.Combine(Path.GetTempPath(), "droiddeck-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
        });

        Assert.False(result.Succeeded);
        Assert.NotNull(result.StartError);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task AppliesEnvironmentOverrides()
    {
        var request = TestProcessHelper.Create("env", "DROIDDECK_TEST_VALUE") with
        {
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DROIDDECK_TEST_VALUE"] = "42",
            },
        };

        var result = await Runner.RunAsync(request);

        Assert.Equal("42", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task UsesWorkingDirectory()
    {
        using var directory = new TempDirectory();
        var result = await Runner.RunAsync(TestProcessHelper.Create("cwd") with
        {
            WorkingDirectory = directory.Path,
        });

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(directory.Path, result.StandardOutput.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task HandlesUnicodeOutput()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("unicode"));

        Assert.Contains("héllo → 世界", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesStandardInput()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("stdin") with
        {
            StandardInput = "line one\nline two\n",
        });

        Assert.Equal("line one\nline two\n", result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TruncatesOutputAtTheConfiguredLimit()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("big", "10000") with
        {
            MaxOutputCharacters = 200,
        });

        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 200, result.StandardOutput.Length.ToString(CultureInfo.InvariantCulture));
        Assert.Contains("[output truncated]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            1,
            result.StandardOutput.Split("[output truncated]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task BoundsMemoryForASingleUnterminatedLine()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("long-line", "2000000"));

        Assert.True(result.Succeeded, result.StartError);
        Assert.True(result.StandardOutput.Length < 100_000, result.StandardOutput.Length.ToString(CultureInfo.InvariantCulture));
        Assert.Contains("[line truncated]", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsOutputLinesAsTheyArrive()
    {
        var progress = new CollectingProgress();
        var result = await Runner.RunAsync(TestProcessHelper.Create("both", "out", "err"), progress);

        Assert.True(result.Succeeded, result.StandardError);
        var lines = progress.Snapshot();
        Assert.Contains(lines, line => line.Stream == ProcessOutputChannel.StandardOutput && line.Text == "out");
        Assert.Contains(lines, line => line.Stream == ProcessOutputChannel.StandardError && line.Text == "err");
    }

    [Fact]
    public async Task RejectsNonPositiveOutputLimit()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Runner.RunAsync(
            TestProcessHelper.Create("echo", "hello") with { MaxOutputCharacters = 0 }));
    }

    [Fact]
    public async Task ReturnsCanceledResultWithoutStartingProcess()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await Runner.RunAsync(TestProcessHelper.Create("sleep", "20000"), cancellationToken: cancellation.Token);

        Assert.True(result.WasCanceled);
        Assert.False(result.TimedOut);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task TimesOutAndKillsTheProcessTree()
    {
        var request = TestProcessHelper.Create("spawn", "20000") with
        {
            Timeout = TimeSpan.FromMilliseconds(750),
        };

        var result = await Runner.RunAsync(request);

        Assert.True(result.TimedOut);
        Assert.False(result.WasCanceled);
        Assert.False(result.Succeeded);

        var childLine = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .First(line => line.StartsWith("child:", StringComparison.Ordinal));
        var childId = int.Parse(childLine["child:".Length..], CultureInfo.InvariantCulture);
        await Wait.ForProcessExitAsync(childId, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancelsARunningProcess()
    {
        using var cancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var running = Runner.RunAsync(TestProcessHelper.Create("sleep", "20000"), cancellationToken: cancellation.Token);

        await Task.Delay(300);
        await cancellation.CancelAsync();
        var result = await running;
        stopwatch.Stop();

        Assert.True(result.WasCanceled);
        Assert.False(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task ToleratesBinaryOutput()
    {
        var result = await Runner.RunAsync(TestProcessHelper.Create("binary"));

        Assert.True(result.Succeeded, result.StartError);
        Assert.False(result.TimedOut);
    }

    private sealed class CollectingProgress : IProgress<ProcessOutputLine>
    {
        private readonly List<ProcessOutputLine> _lines = [];

        public void Report(ProcessOutputLine value)
        {
            lock (_lines)
            {
                _lines.Add(value);
            }
        }

        public List<ProcessOutputLine> Snapshot()
        {
            lock (_lines)
            {
                return _lines.ToList();
            }
        }
    }
}
