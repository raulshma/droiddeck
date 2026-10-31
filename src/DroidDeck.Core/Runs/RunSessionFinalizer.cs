namespace DroidDeck.Core.Runs;

/// <summary>What happens to the steps that are still running when a session is finalized.</summary>
internal enum RunningStepDisposition
{
    /// <summary>Running steps keep their current state.</summary>
    Leave,

    /// <summary>Running steps are marked as cancelled.</summary>
    Cancel,

    /// <summary>Running steps are marked as failed with the session error.</summary>
    Fail,
}

/// <summary>Which child processes a terminal transition stops before it completes.</summary>
internal enum SessionResourceDisposition
{
    /// <summary>Stop Flutter, Metro, and logcat.</summary>
    StopAll,

    /// <summary>Stop Metro only, keeping an attached Flutter session and logcat running.</summary>
    StopMetro,

    /// <summary>Leave every child process running, for outcomes where the app stays live.</summary>
    LeaveRunning,
}

/// <summary>What is written when a session reaches its terminal state.</summary>
internal enum SessionPersistence
{
    /// <summary>The in-memory change is raised, but nothing is saved or notified.</summary>
    None,

    /// <summary>The session snapshot is saved without a session notification.</summary>
    Snapshot,

    /// <summary>The snapshot is saved and the session notification is raised.</summary>
    SnapshotAndNotification,
}

/// <summary>One terminal transition applied by <see cref="RunSessionFinalizer"/>.</summary>
internal sealed class SessionOutcome
{
    /// <summary>Gets the terminal session state.</summary>
    public RunSessionState State { get; init; }

    /// <summary>Gets the error text, or null to keep the error already recorded.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the status message shown for the finished session.</summary>
    public required string StatusMessage { get; init; }

    /// <summary>Gets what happens to steps that are still running.</summary>
    public RunningStepDisposition RunningSteps { get; init; }

    /// <summary>Gets the step whose failure triggered the transition.</summary>
    public RunStep? FailedStep { get; init; }

    /// <summary>Gets the exit code recorded on the failed step.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets which child processes are stopped before the transition.</summary>
    public SessionResourceDisposition Resources { get; init; } = SessionResourceDisposition.StopAll;

    /// <summary>Gets what is persisted after the transition.</summary>
    public SessionPersistence Persistence { get; init; } = SessionPersistence.SnapshotAndNotification;
}

internal interface IRunSessionFinalizationEffects
{
    Task ReleaseResourcesAsync(RunContext? context, SessionResourceDisposition disposition);

    void RaiseChanged(RunSession session);

    Task PersistAsync(RunSession session, SessionPersistence persistence);
}

/// <summary>
/// Applies terminal outcomes to a run session exactly once. The first caller wins; a racing stop,
/// cancellation, or timeout observes the finished session and leaves it untouched.
/// </summary>
internal sealed class RunSessionFinalizer
{
    private readonly IRunSessionFinalizationEffects _effects;

    public RunSessionFinalizer(IRunSessionFinalizationEffects effects)
    {
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
    }

    /// <summary>
    /// Releases resources, then applies the transition under the session lock, then notifies and
    /// persists. Resources are released even when another caller already finished the session, but
    /// only the first caller transitions, notifies, and persists.
    /// </summary>
    /// <returns><see langword="true"/> when this call performed the session transition.</returns>
    public async Task<bool> FinalizeAsync(RunSession session, RunContext? context, SessionOutcome outcome)
    {
        await _effects.ReleaseResourcesAsync(context, outcome.Resources).ConfigureAwait(false);

        if (!TryFinalizeSession(session, outcome))
        {
            return false;
        }

        _effects.RaiseChanged(session);
        await _effects.PersistAsync(session, outcome.Persistence).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Performs the terminal transition under the session lock: running steps are swept into the
    /// outcome, the failing step records its error and exit code, and the end time is set once.
    /// A session that already finished is left completely untouched, including its steps.
    /// </summary>
    private static bool TryFinalizeSession(RunSession session, SessionOutcome outcome)
    {
        lock (session)
        {
            if (session.IsFinished)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            if (outcome.FailedStep is { } failedStep)
            {
                failedStep.State = RunStepState.Failed;
                failedStep.EndedAt = now;
                failedStep.Duration = failedStep.StartedAt is { } started ? now - started : null;
                if (outcome.Error is not null)
                {
                    failedStep.Error = outcome.Error;
                }

                if (outcome.ExitCode is not null)
                {
                    failedStep.ExitCode = outcome.ExitCode;
                }
            }

            SweepRunningSteps(session, outcome.RunningSteps, outcome.Error, now);
            session.State = outcome.State;
            if (outcome.Error is not null)
            {
                session.Error = outcome.Error;
            }

            session.StatusMessage = outcome.StatusMessage;
            CompleteSession(session);
            return true;
        }
    }

    private static void SweepRunningSteps(
        RunSession session,
        RunningStepDisposition disposition,
        string? error,
        DateTimeOffset now)
    {
        if (disposition == RunningStepDisposition.Leave)
        {
            return;
        }

        foreach (var step in session.Steps)
        {
            if (step.State != RunStepState.Running)
            {
                continue;
            }

            if (disposition == RunningStepDisposition.Cancel)
            {
                step.State = RunStepState.Cancelled;
            }
            else
            {
                step.State = RunStepState.Failed;
                if (error is not null)
                {
                    step.Error ??= error;
                }
            }

            step.EndedAt = now;
            step.Duration = step.StartedAt is { } started ? now - started : null;
        }
    }

    private static void CompleteSession(RunSession session)
    {
        var now = DateTimeOffset.UtcNow;
        session.EndedAt = now;
        session.Duration = now >= session.StartedAt ? now - session.StartedAt : TimeSpan.Zero;
    }
}
