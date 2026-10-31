using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;

namespace DroidDeck.Core.Tests.Runs;

public sealed class RunSessionFinalizerTests
{
    [Fact]
    public async Task FinalizeAsync_ReleasesResourcesTransitionsNotifiesAndPersistsInOrder()
    {
        var calls = new List<string>();
        var effects = new RecordingEffects(calls);
        var session = CreateSession(RunningStep("build"));
        var context = CreateContext(session);
        var finalizer = new RunSessionFinalizer(effects);

        var finalized = await finalizer.FinalizeAsync(
            session,
            context,
            new SessionOutcome
            {
                State = RunSessionState.Failed,
                Error = "boom",
                StatusMessage = "The build failed.",
            });

        Assert.True(finalized);
        Assert.Equal(["release", "changed", "persist"], calls);
        Assert.Equal([RunSessionState.Running], effects.StatesAtRelease);
        Assert.Equal([RunSessionState.Failed], effects.StatesAtChanged);
        Assert.Equal([SessionResourceDisposition.StopAll], effects.ReleasedDispositions);
        Assert.Same(context, Assert.Single(effects.ReleasedContexts));
        Assert.Same(session, Assert.Single(effects.ChangedSessions));
        Assert.Equal([SessionPersistence.SnapshotAndNotification], effects.PersistedDispositions);
        Assert.Equal(RunSessionState.Failed, session.State);
        Assert.Equal("The build failed.", session.StatusMessage);
        Assert.NotNull(session.EndedAt);
        Assert.NotNull(session.Duration);
    }

    [Fact]
    public async Task FinalizeAsync_FirstCallerWinsAndStillReleasesResourcesOnLaterCalls()
    {
        var calls = new List<string>();
        var effects = new RecordingEffects(calls);
        var step = RunningStep("build");
        var session = CreateSession(step);
        var finalizer = new RunSessionFinalizer(effects);

        var first = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Succeeded,
                StatusMessage = "Done.",
                RunningSteps = RunningStepDisposition.Leave,
                Resources = SessionResourceDisposition.LeaveRunning,
                Persistence = SessionPersistence.None,
            });
        var second = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Cancelled,
                Error = "The run was cancelled.",
                StatusMessage = "The run was cancelled.",
                RunningSteps = RunningStepDisposition.Cancel,
            });

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(["release", "changed", "persist", "release"], calls);
        Assert.Equal(
            [SessionResourceDisposition.LeaveRunning, SessionResourceDisposition.StopAll],
            effects.ReleasedDispositions);
        Assert.Single(effects.ChangedSessions);
        Assert.Single(effects.PersistedDispositions);
        Assert.Equal(RunSessionState.Succeeded, session.State);
        Assert.Equal("Done.", session.StatusMessage);
        Assert.Null(session.Error);
        Assert.Equal(RunStepState.Running, step.State);
        Assert.Null(step.EndedAt);
        Assert.Null(step.Duration);
    }

    [Fact]
    public async Task FinalizeAsync_FailedStepRecordsErrorExitCodeAndEndTime()
    {
        var step = RunningStep("build");
        step.StartedAt = DateTimeOffset.UtcNow.AddSeconds(-3);
        var session = CreateSession(step);
        var finalizer = new RunSessionFinalizer(new RecordingEffects([]));

        var finalized = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Failed,
                Error = "The build failed.",
                StatusMessage = "Build failed.",
                FailedStep = step,
                ExitCode = 17,
            });

        Assert.True(finalized);
        Assert.Equal(RunStepState.Failed, step.State);
        Assert.Equal("The build failed.", step.Error);
        Assert.Equal(17, step.ExitCode);
        Assert.NotNull(step.EndedAt);
        Assert.True(step.Duration >= TimeSpan.FromSeconds(3));
        Assert.Equal(RunSessionState.Failed, session.State);
        Assert.Equal("The build failed.", session.Error);
        Assert.Equal("Build failed.", session.StatusMessage);
        Assert.NotNull(session.EndedAt);
        Assert.NotNull(session.Duration);
    }

    [Fact]
    public async Task FinalizeAsync_CancelSweepsRunningStepsToCancelled()
    {
        var build = RunningStep("build");
        var install = RunningStep("install");
        var session = CreateSession(build, install);
        var finalizer = new RunSessionFinalizer(new RecordingEffects([]));

        var finalized = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Cancelled,
                Error = "The run was cancelled.",
                StatusMessage = "The run was cancelled.",
                RunningSteps = RunningStepDisposition.Cancel,
            });

        Assert.True(finalized);
        Assert.Equal(RunSessionState.Cancelled, session.State);
        Assert.Equal("The run was cancelled.", session.Error);
        foreach (var step in new[] { build, install })
        {
            Assert.Equal(RunStepState.Cancelled, step.State);
            Assert.NotNull(step.EndedAt);
            Assert.NotNull(step.Duration);
            Assert.Null(step.Error);
        }
    }

    [Fact]
    public async Task FinalizeAsync_FailSweepsRunningStepsToFailedWithTheSessionError()
    {
        var withoutError = RunningStep("build");
        var withError = RunningStep("install");
        withError.Error = "The install said no.";
        var session = CreateSession(withoutError, withError);
        var finalizer = new RunSessionFinalizer(new RecordingEffects([]));

        var finalized = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Failed,
                Error = "The session failed.",
                StatusMessage = "The session failed.",
                RunningSteps = RunningStepDisposition.Fail,
            });

        Assert.True(finalized);
        Assert.Equal(RunSessionState.Failed, session.State);
        Assert.Equal("The session failed.", session.Error);
        Assert.Equal(RunStepState.Failed, withoutError.State);
        Assert.Equal("The session failed.", withoutError.Error);
        Assert.Equal(RunStepState.Failed, withError.State);
        Assert.Equal("The install said no.", withError.Error);
        Assert.NotNull(withoutError.EndedAt);
        Assert.NotNull(withError.EndedAt);
    }

    [Fact]
    public async Task FinalizeAsync_LeaveKeepsRunningStepsRunning()
    {
        var step = RunningStep("build");
        var session = CreateSession(step);
        var finalizer = new RunSessionFinalizer(new RecordingEffects([]));

        var finalized = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Succeeded,
                StatusMessage = "The app is running.",
                Resources = SessionResourceDisposition.LeaveRunning,
            });

        Assert.True(finalized);
        Assert.Equal(RunSessionState.Succeeded, session.State);
        Assert.NotNull(session.EndedAt);
        Assert.Equal(RunStepState.Running, step.State);
        Assert.Null(step.EndedAt);
        Assert.Null(step.Duration);
        Assert.Null(step.Error);
    }

    [Fact]
    public async Task FinalizeAsync_ForwardsEveryPersistenceDisposition()
    {
        foreach (var persistence in new[]
                 {
                     SessionPersistence.None,
                     SessionPersistence.Snapshot,
                     SessionPersistence.SnapshotAndNotification,
                 })
        {
            var effects = new RecordingEffects([]);
            var session = CreateSession();
            var finalizer = new RunSessionFinalizer(effects);

            var finalized = await finalizer.FinalizeAsync(
                session,
                null,
                new SessionOutcome
                {
                    State = RunSessionState.Succeeded,
                    StatusMessage = "Done.",
                    Persistence = persistence,
                });

            Assert.True(finalized);
            Assert.Equal([persistence], effects.PersistedDispositions);
            Assert.Same(session, Assert.Single(effects.ChangedSessions));
        }
    }

    [Fact]
    public async Task FinalizeAsync_AlreadyFinishedSessionIsUntouched()
    {
        var endedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var step = RunningStep("build");
        var session = CreateSession(step);
        session.State = RunSessionState.Succeeded;
        session.StatusMessage = "Already done.";
        session.EndedAt = endedAt;
        session.Duration = TimeSpan.FromSeconds(4);
        var calls = new List<string>();
        var effects = new RecordingEffects(calls);
        var finalizer = new RunSessionFinalizer(effects);

        var finalized = await finalizer.FinalizeAsync(
            session,
            null,
            new SessionOutcome
            {
                State = RunSessionState.Failed,
                Error = "A late failure.",
                StatusMessage = "Too late.",
                RunningSteps = RunningStepDisposition.Fail,
                Persistence = SessionPersistence.Snapshot,
            });

        Assert.False(finalized);
        Assert.Equal(["release"], calls);
        Assert.Empty(effects.ChangedSessions);
        Assert.Empty(effects.PersistedDispositions);
        Assert.Equal(RunSessionState.Succeeded, session.State);
        Assert.Equal("Already done.", session.StatusMessage);
        Assert.Null(session.Error);
        Assert.Equal(endedAt, session.EndedAt);
        Assert.Equal(TimeSpan.FromSeconds(4), session.Duration);
        Assert.Equal(RunStepState.Running, step.State);
        Assert.Null(step.EndedAt);
        Assert.Null(step.Error);
    }

    private static RunSession CreateSession(params RunStep[] steps) => new()
    {
        ProjectId = "proj-1",
        ProjectName = "Sample",
        ConfigurationId = "debug",
        ConfigurationName = "Debug",
        State = RunSessionState.Running,
        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
        Steps = [.. steps],
    };

    private static RunStep RunningStep(string id) => new()
    {
        Id = id,
        Name = id,
        State = RunStepState.Running,
        StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
    };

    private static RunContext CreateContext(RunSession session) => new(
        session,
        new RunRequest
        {
            Project = new ProjectModel
            {
                Id = session.ProjectId,
                Name = session.ProjectName,
                RootPath = session.ProjectRoot,
            },
            Configuration = new RunConfiguration
            {
                Id = session.ConfigurationId,
                Name = session.ConfigurationName,
            },
        });

    private sealed class RecordingEffects : IRunSessionFinalizationEffects
    {
        private readonly List<string> _calls;

        public RecordingEffects(List<string> calls) => _calls = calls;

        public List<SessionResourceDisposition> ReleasedDispositions { get; } = [];

        public List<RunContext?> ReleasedContexts { get; } = [];

        public List<RunSessionState?> StatesAtRelease { get; } = [];

        public List<RunSession> ChangedSessions { get; } = [];

        public List<RunSessionState> StatesAtChanged { get; } = [];

        public List<SessionPersistence> PersistedDispositions { get; } = [];

        public Task ReleaseResourcesAsync(RunContext? context, SessionResourceDisposition disposition)
        {
            _calls.Add("release");
            ReleasedDispositions.Add(disposition);
            ReleasedContexts.Add(context);
            StatesAtRelease.Add(context?.Session.State);
            return Task.CompletedTask;
        }

        public void RaiseChanged(RunSession session)
        {
            _calls.Add("changed");
            ChangedSessions.Add(session);
            StatesAtChanged.Add(session.State);
        }

        public Task PersistAsync(RunSession session, SessionPersistence persistence)
        {
            _calls.Add("persist");
            PersistedDispositions.Add(persistence);
            return Task.CompletedTask;
        }
    }
}
