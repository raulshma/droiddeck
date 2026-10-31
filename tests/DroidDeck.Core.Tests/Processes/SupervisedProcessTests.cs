using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using DroidDeck.Core.Processes;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Processes;

public sealed class SupervisedProcessTests
{
    [Fact]
    public async Task DeliversStandardOutputAndStandardErrorLines()
    {
        var lines = new ConcurrentQueue<ProcessOutputLine>();
        var process = SupervisedProcess.Start(
            TestProcessHelper.Create("both", "out line", "err line"),
            lines.Enqueue);
        try
        {
            await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));
            await process.StopAsync(TimeSpan.FromSeconds(5));

            Assert.True(process.HasExited);
            Assert.True(process.ProcessId > 0);
            Assert.Contains(
                lines,
                line => line.Stream == ProcessOutputChannel.StandardOutput &&
                        line.Text.Contains("out line", StringComparison.Ordinal));
            Assert.Contains(
                lines,
                line => line.Stream == ProcessOutputChannel.StandardError &&
                        line.Text.Contains("err line", StringComparison.Ordinal));
            Assert.All(lines, line => Assert.NotEqual(default, line.Timestamp));
            Assert.Contains("err line", process.StderrTail);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    [Fact]
    public async Task RetainsAndFormatsOnlyTheLastTwentyStderrLines()
    {
        var stderr = string.Join("\n", Enumerable.Range(1, 25).Select(index => $"error {index}"));
        var process = SupervisedProcess.Start(TestProcessHelper.Create("both", "out", stderr));
        try
        {
            await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));
            await process.StopAsync(TimeSpan.FromSeconds(5));

            var tail = process.StderrTail;
            Assert.Equal(SupervisedProcess.StderrTailCapacity, tail.Count);
            Assert.Equal("error 6", tail[0]);
            Assert.Equal("error 25", tail[^1]);

            var formatted = process.FormatStderrTail();
            Assert.StartsWith(Environment.NewLine, formatted, StringComparison.Ordinal);
            Assert.Contains("error 6", formatted, StringComparison.Ordinal);
            Assert.Contains("error 25", formatted, StringComparison.Ordinal);
            Assert.DoesNotContain("error 5", formatted, StringComparison.Ordinal);
            Assert.Equal(
                SupervisedProcess.StderrTailCapacity,
                formatted.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopsGracefullyWhenClosingStandardInputEndsTheProcess()
    {
        var process = SupervisedProcess.Start(TestProcessHelper.Create("stdin"));
        try
        {
            Assert.False(process.HasExited);

            var elapsed = Stopwatch.StartNew();
            await process.StopAsync(TimeSpan.FromSeconds(30));
            elapsed.Stop();

            Assert.True(process.HasExited);
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(15),
                $"The process should exit as soon as standard input closes, but stopping took {elapsed.Elapsed}.");
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    [Fact]
    public async Task KillsTheProcessTreeWhenTheGracePeriodExpires()
    {
        var childId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = SupervisedProcess.Start(
            TestProcessHelper.Create("spawn", "60000"),
            line =>
            {
                if (line.Stream == ProcessOutputChannel.StandardOutput &&
                    line.Text.StartsWith("child:", StringComparison.Ordinal) &&
                    int.TryParse(
                        line.Text["child:".Length..],
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var id))
                {
                    childId.TrySetResult(id);
                }
            });
        try
        {
            var id = await childId.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(process.HasExited);

            var elapsed = Stopwatch.StartNew();
            await process.StopAsync(TimeSpan.FromMilliseconds(250));
            elapsed.Stop();

            Assert.True(process.HasExited);
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(20),
                $"Stopping the process should not wait for the grace period to elapse twice, but took {elapsed.Elapsed}.");
            await Wait.ForProcessExitAsync(id, TimeSpan.FromSeconds(10));
        }
        finally
        {
            await process.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeIsIdempotentAndStopsTheProcess()
    {
        var process = SupervisedProcess.Start(TestProcessHelper.Create("stdin"));

        await process.DisposeAsync();
        Assert.True(process.HasExited);

        await process.DisposeAsync();
        await process.StopAsync(TimeSpan.FromSeconds(1));
        Assert.True(process.HasExited);
    }
}
