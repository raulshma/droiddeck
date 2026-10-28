using DroidDeck.Core;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Sessions;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Sessions;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task SaveAndLoad_RestoresSessionReadOnly()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(new AppPaths(temp.Path));
        var session = CreateSession();
        session.Steps.Add(new RunStep
        {
            Id = "build",
            Name = "Build",
            Kind = RunStepKind.Build,
            State = RunStepState.Succeeded,
            CommandLine = "gradlew.bat assembleDebug",
            ExitCode = 0,
        });
        session.BuildOutput.AddRange(["BUILD SUCCESSFUL", "> Task :app:assembleDebug"]);
        session.LogEntries.Add(LogcatParser.ParseLine(
            "2026-10-28 10:00:00.000  123  456 I Sample: hello",
            session.StartedAt)!);

        await store.SaveAsync(session);
        var loaded = await store.LoadAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.True(loaded.IsReadOnly);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(session.ProjectName, loaded.ProjectName);
        Assert.Equal(["BUILD SUCCESSFUL", "> Task :app:assembleDebug"], loaded.BuildOutput);
        var entry = Assert.Single(loaded.LogEntries);
        Assert.Equal("Sample", entry.Tag);
        Assert.Equal("hello", entry.Message);
        Assert.Equal(1L, entry.Sequence);
        var step = Assert.Single(loaded.Steps);
        Assert.Equal(RunStepState.Succeeded, step.State);
    }

    [Fact]
    public async Task List_ReturnsSummariesNewestFirst()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(new AppPaths(temp.Path));
        var older = CreateSession();
        older.StartedAt = DateTimeOffset.UtcNow.AddHours(-2);
        older.State = RunSessionState.Failed;
        older.Error = "install failed";
        var newer = CreateSession();
        newer.StartedAt = DateTimeOffset.UtcNow.AddHours(-1);
        newer.State = RunSessionState.Succeeded;

        await store.SaveAsync(older);
        await store.SaveAsync(newer);
        var summaries = await store.ListAsync();

        Assert.Equal(2, summaries.Count);
        Assert.Equal(newer.Id, summaries[0].Id);
        Assert.Equal(RunSessionState.Succeeded, summaries[0].State);
        Assert.Equal("install failed", summaries[1].Error);
        Assert.NotNull(summaries[0].DirectoryPath);
    }

    [Fact]
    public async Task ExportTextAndJson_RedactsSecretsByDefault()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(new AppPaths(temp.Path));
        var session = CreateSession();
        session.Steps.Add(new RunStep
        {
            Id = "install",
            Name = "Install",
            Kind = RunStepKind.Install,
            Error = "failed with password=hunter2",
        });
        session.LogEntries.Add(new LogEntry
        {
            Message = "Authorization: Bearer abc123",
            Raw = "Authorization: Bearer abc123",
        });

        var text = await store.ExportAsync(session, SessionExportFormat.Text);
        var json = await store.ExportAsync(session, SessionExportFormat.Json);
        var unredacted = await store.ExportAsync(session, SessionExportFormat.Text, redact: false);

        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("abc123", text);
        Assert.Contains("<redacted>", text);
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("abc123", json);
        Assert.Contains("hunter2", unredacted);
    }

    [Fact]
    public async Task ApplyRetention_RemovesSessionsOlderThanTheLimit()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new JsonSessionStore(paths);
        var old = CreateSession();
        old.StartedAt = DateTimeOffset.UtcNow.AddDays(-30);
        var recent = CreateSession();
        await store.SaveAsync(old);
        await store.SaveAsync(recent);
        var summaries = await store.ListAsync();
        var oldDirectory = summaries.Single(summary => summary.Id == old.Id).DirectoryPath!;
        Directory.SetLastWriteTimeUtc(oldDirectory, DateTime.UtcNow.AddDays(-30));

        var result = await store.ApplyRetentionAsync(retentionDays: 14, maxBytes: 0);

        Assert.Equal(1, result.RemovedSessions);
        Assert.Contains(Path.GetFileName(oldDirectory), result.Removed);
        Assert.Null(await store.LoadAsync(old.Id));
        Assert.NotNull(await store.LoadAsync(recent.Id));
    }

    [Fact]
    public async Task ApplyRetention_TrimsToTheByteLimit()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        var store = new JsonSessionStore(paths);
        var first = CreateSession();
        first.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var second = CreateSession();
        second.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.SaveAsync(first);
        await store.SaveAsync(second);
        var summaries = await store.ListAsync();
        Directory.SetLastWriteTimeUtc(summaries[1].DirectoryPath!, DateTime.UtcNow.AddMinutes(-10));
        Directory.SetLastWriteTimeUtc(summaries[0].DirectoryPath!, DateTime.UtcNow.AddMinutes(-5));

        var result = await store.ApplyRetentionAsync(retentionDays: 14, maxBytes: 1);

        Assert.True(result.RemovedSessions >= 1);
        Assert.True(result.RemovedBytes > 0);
    }

    [Fact]
    public async Task Delete_RemovesTheSessionFolder()
    {
        using var temp = new TempDirectory();
        var store = new JsonSessionStore(new AppPaths(temp.Path));
        var session = CreateSession();
        await store.SaveAsync(session);

        await store.DeleteAsync(session.Id);

        Assert.Null(await store.LoadAsync(session.Id));
        Assert.Empty(await store.ListAsync());
    }

    private static RunSession CreateSession() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ProjectId = "project",
        ProjectName = "Sample",
        ProjectRoot = @"C:\repo\sample",
        ConfigurationId = "debug",
        ConfigurationName = "Debug",
        DeviceSerial = "emulator-5554",
        DeviceName = "Pixel",
        StartedAt = DateTimeOffset.UtcNow,
        EndedAt = DateTimeOffset.UtcNow.AddMinutes(1),
        Duration = TimeSpan.FromMinutes(1),
        State = RunSessionState.Succeeded,
    };
}
