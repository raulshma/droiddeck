using DroidDeck.Plugins;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginAuditLogTests
{
    [Fact]
    public async Task AppendedEntriesRoundTripEveryField()
    {
        using var temp = new TempDirectory();
        var log = new PluginAuditLog(temp.Combine("logs", "plugin-audit.jsonl"));
        var timestamp = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        await log.AppendAsync(new PluginAuditEntry(
            timestamp,
            "install",
            "com.example.chart",
            FromVersion: null,
            ToVersion: "1.0.0",
            Result: "success",
            Reason: null,
            SourceId: "src-abc123"));
        await log.AppendAsync(new PluginAuditEntry(
            timestamp.AddMinutes(1),
            "update",
            "com.example.chart",
            FromVersion: "1.0.0",
            ToVersion: "1.1.0",
            Result: "failure",
            Reason: "The package hash does not match the catalog.",
            SourceId: "src-abc123"));

        var entries = await log.ReadAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal(timestamp, entries[0].TimestampUtc);
        Assert.Equal("install", entries[0].Action);
        Assert.Equal("com.example.chart", entries[0].PluginId);
        Assert.Null(entries[0].FromVersion);
        Assert.Equal("1.0.0", entries[0].ToVersion);
        Assert.Equal("success", entries[0].Result);
        Assert.Null(entries[0].Reason);
        Assert.Equal("src-abc123", entries[0].SourceId);
        Assert.Equal("update", entries[1].Action);
        Assert.Equal("1.0.0", entries[1].FromVersion);
        Assert.Equal("1.1.0", entries[1].ToVersion);
        Assert.Equal("failure", entries[1].Result);
        Assert.Equal("The package hash does not match the catalog.", entries[1].Reason);
    }

    [Fact]
    public async Task TimestampsAreStoredAsUtcIso8601WithZeroOffset()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("logs", "plugin-audit.jsonl");
        var log = new PluginAuditLog(file);
        var local = new DateTimeOffset(2026, 10, 30, 14, 30, 0, TimeSpan.FromHours(2));

        await log.AppendAsync(new PluginAuditEntry(
            local, "rollback", "com.example.chart",
            FromVersion: "1.1.0", ToVersion: "1.0.0",
            Result: "success", Reason: null, SourceId: null));

        var line = await File.ReadAllTextAsync(file);
        Assert.Contains("\"timestamp\":\"2026-10-30T12:30:00.000Z\"", line, StringComparison.Ordinal);

        var entry = Assert.Single(await log.ReadAsync());
        Assert.Equal(TimeSpan.Zero, entry.TimestampUtc.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 30, 0, TimeSpan.Zero), entry.TimestampUtc);
    }

    [Fact]
    public async Task ACorruptOrEmptyLineIsSkippedWhenReading()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("logs", "plugin-audit.jsonl");
        Directory.CreateDirectory(temp.Combine("logs"));
        await File.WriteAllLinesAsync(file,
        [
            "not json at all",
            "{\"timestamp\":\"2026-10-30T12:00:00.000Z\",\"action\":\"install\",\"pluginId\":\"com.example.chart\",\"toVersion\":\"1.0.0\",\"result\":\"success\"}",
            string.Empty,
        ]);

        var entries = await new PluginAuditLog(file).ReadAsync();

        Assert.Single(entries);
        Assert.Equal("com.example.chart", entries[0].PluginId);
    }

    [Fact]
    public async Task ReadingAMissingFileYieldsNoEntries()
    {
        using var temp = new TempDirectory();

        var entries = await new PluginAuditLog(temp.Combine("logs", "plugin-audit.jsonl")).ReadAsync();

        Assert.Empty(entries);
    }
}
