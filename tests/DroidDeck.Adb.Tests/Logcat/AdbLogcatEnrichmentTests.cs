using DroidDeck.Adb.Logcat;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Adb.Tests.Logcat;

public sealed class AdbLogcatEnrichmentTests
{
    [Fact]
    public async Task LogcatEntriesCarryParserEnrichment()
    {
        var runner = new FakeProcessRunner
        {
            OutputLines = _ =>
            [
                new ProcessOutputLine(
                    ProcessOutputChannel.StandardOutput,
                    "10-28 12:00:00.000  1234  1234 E AndroidRuntime: FATAL EXCEPTION: main",
                    DateTimeOffset.Now),
            ],
        };
        var registry = new TestPluginRegistry();
        registry.AddLogParser(new StubParser());
        using var enricher = new PluginLogEnricher(registry);
        await using var service = new AdbLogcatService(
            runner,
            new NullDeviceCommands(),
            () => "adb",
            enricher: enricher);

        var session = await service.StartAsync(new LogcatRequest
        {
            DeviceSerial = "emulator-5554",
            AppOnly = false,
        });

        await Wait.UntilAsync(() => session.Entries.Count > 0);
        var entry = Assert.Single(session.Entries);

        Assert.Equal("Unity", entry.Tag);
        Assert.Equal("FATAL EXCEPTION: main", entry.Message);
        Assert.Equal("fatal", entry.HighlightRuleId);
    }

    private sealed class StubParser : ILogParser
    {
        public string Id => "plugin.unity";

        public string Name => "Unity parser";

        public int Priority => 0;

        public LogParserResult? Parse(LogParserContext context) =>
            context.Tag == "AndroidRuntime"
                ? new LogParserResult(Tag: "Unity", HighlightRuleId: "fatal")
                : null;
    }

    private sealed class NullDeviceCommands : IDeviceCommandService
    {
        public string AdbExecutable => "adb";

        public Task<DeviceCommandResult> InstallAsync(
            string serial,
            IReadOnlyList<string> apkPaths,
            AdbInstallOptions options,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default) => Ok();

        public Task<DeviceCommandResult> LaunchAsync(
            string serial,
            AdbLaunchOptions options,
            CancellationToken cancellationToken = default) => Ok();

        public Task<DeviceCommandResult> ForceStopAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => Ok();

        public Task<DeviceCommandResult> UninstallAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => Ok();

        public Task<DeviceCommandResult> ShellAsync(
            string serial,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) => Ok();

        public Task<DeviceCommandResult> ReverseAsync(
            string serial,
            int devicePort,
            int hostPort,
            CancellationToken cancellationToken = default) => Ok();

        public Task<string?> ResolveActivityAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> GetPackageUidAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);

        public Task<long?> GetFreeStorageAsync(
            string serial,
            CancellationToken cancellationToken = default) => Task.FromResult<long?>(null);

        public Task<DeviceCommandResult> ClearLogcatAsync(
            string serial,
            CancellationToken cancellationToken = default) => Ok();

        private static Task<DeviceCommandResult> Ok() =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });
    }
}
