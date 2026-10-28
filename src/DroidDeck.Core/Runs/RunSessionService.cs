using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Notifications;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Resources;
using DroidDeck.Core.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Runs;

/// <summary>
/// Executes the build, artifact, install, launch, custom step, and logcat pipeline
/// for one project configuration on one device.
/// </summary>
public sealed class RunSessionService : IRunSessionService, IAsyncDisposable
{
    private const int MaxBuildOutputLines = 100_000;
    private const int MaxStepOutputTailLines = 200;
    private const int MaxLogEntries = 1_000_000;
    private static readonly StringComparer SerialComparer = StringComparer.Ordinal;

    private readonly IRunPlanBuilder _planBuilder;
    private readonly IArtifactResolver _artifacts;
    private readonly IDeviceCommandService _commands;
    private readonly ILogcatService _logcat;
    private readonly ISessionStore _sessionStore;
    private readonly INotificationService _notifications;
    private readonly IProcessRunner _runner;
    private readonly IResourceGuard? _resourceGuard;
    private readonly IDeviceOperationCoordinator? _coordinator;
    private readonly ILogger<RunSessionService> _logger;
    private readonly MetroSession.PortProbe? _metroProbe;
    private readonly MetroSession.ReloadSender? _metroReload;
    private readonly List<RunSession> _sessions = [];
    private readonly Dictionary<string, RunContext> _contexts = new(SerialComparer);
    private readonly Dictionary<int, MetroLease> _metroByPort = new();
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates the run orchestrator.</summary>
    public RunSessionService(
        IRunPlanBuilder planBuilder,
        IArtifactResolver artifacts,
        IDeviceCommandService commands,
        ILogcatService logcat,
        ISessionStore sessionStore,
        INotificationService notifications,
        IProcessRunner runner,
        IResourceGuard? resourceGuard = null,
        IDeviceOperationCoordinator? coordinator = null,
        ILogger<RunSessionService>? logger = null,
        MetroSession.PortProbe? metroProbe = null,
        MetroSession.ReloadSender? metroReload = null)
    {
        _planBuilder = planBuilder ?? throw new ArgumentNullException(nameof(planBuilder));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _logcat = logcat ?? throw new ArgumentNullException(nameof(logcat));
        _sessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _resourceGuard = resourceGuard;
        _coordinator = coordinator;
        _logger = logger ?? NullLogger<RunSessionService>.Instance;
        _metroProbe = metroProbe;
        _metroReload = metroReload;
    }

    /// <inheritdoc />
    public IReadOnlyList<RunSession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<RunSessionChangedEventArgs>? SessionChanged;

    /// <inheritdoc />
    public event EventHandler<RunOutputEventArgs>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<LogEntriesEventArgs>? LogEntriesReceived;

    /// <inheritdoc />
    public event EventHandler<CrashDetectedEventArgs>? CrashDetected;

    /// <inheritdoc />
    public async Task<RunSession> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = CreateSession(request);
        var context = new RunContext(session, request);
        lock (_sync)
        {
            _sessions.Insert(0, session);
            _contexts[session.Id] = context;
        }

        RaiseChanged(session);

        if (request.DryRun)
        {
            CompleteDryRun(session, request);
            return session;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Cancellation.Token);
        context.Linked = linked;
        try
        {
            await ExecuteAsync(context, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await FinishCancelledAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            _logger.LogError(exception, "Run session {SessionId} failed unexpectedly.", session.Id);
            await FailSessionAsync(context, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            context.Linked = null;
        }

        return session;
    }

    /// <inheritdoc />
    public RunSession? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_sync)
        {
            return _sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var context = FindContext(sessionId);
        if (context is null)
        {
            return;
        }

        context.Cancellation.Cancel();
        await StopFlutterAsync(context).ConfigureAwait(false);
        await StopMetroAsync(context).ConfigureAwait(false);
        await DetachLogcatAsync(context).ConfigureAwait(false);
        await FinishCancelledAsync(context).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async Task StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var context = FindContext(sessionId);
        if (context is null)
        {
            return;
        }

        var session = context.Session;
        await StopFlutterAsync(context).ConfigureAwait(false);
        await StopMetroAsync(context).ConfigureAwait(false);
        await DetachLogcatAsync(context).ConfigureAwait(false);
        if (session.IsFinished)
        {
            RaiseChanged(session);
            return;
        }

        if (session.State == RunSessionState.Running)
        {
            lock (session)
            {
                session.State = RunSessionState.Succeeded;
                session.StatusMessage = "Stopped by the user.";
                CompleteSession(session);
            }

            RaiseChanged(session);
            await SaveAndNotifyAsync(session).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        context.StoppedByUser = true;
        context.Cancellation.Cancel();
        await FinishCancelledAsync(context).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async Task RestartAppAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var context = FindContext(sessionId);
        var session = context?.Session ?? GetSession(sessionId);
        if (session is null)
        {
            throw new InvalidOperationException($"Run session \"{sessionId}\" was not found.");
        }

        if (session.IsReadOnly)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(session.PackageName))
        {
            session.StatusMessage = "The package name is unknown. Run the app once before restarting it.";
            RaiseChanged(session);
            return;
        }

        if (string.IsNullOrWhiteSpace(session.DeviceSerial))
        {
            session.StatusMessage = "The device is unknown. Run the app again with a selected device.";
            RaiseChanged(session);
            return;
        }

        var step = AddStep(session, $"restart-{Guid.NewGuid():N}", "Restart app", RunStepKind.Launch);
        SetSessionState(session, RunSessionState.Launching, "Restarting the application.");
        StartStep(session, step);
        try
        {
            await using var deviceLease = await AcquireDeviceLockAsync(
                session.DeviceSerial,
                $"Restart {session.PackageName} on {session.DeviceSerial}",
                alreadyHeld: false,
                cancellationToken).ConfigureAwait(false);
            await _commands.ForceStopAsync(session.DeviceSerial, session.PackageName, cancellationToken).ConfigureAwait(false);
            var result = await _commands.LaunchAsync(
                session.DeviceSerial,
                new AdbLaunchOptions
                {
                    PackageName = session.PackageName,
                    Activity = session.Activity,
                    Wait = true,
                },
                cancellationToken).ConfigureAwait(false);
            step.CommandLine = result.CommandLine;
            step.OutputTail = Tail(result.Output);
            if (!result.Succeeded)
            {
                await FailStepAsync(
                    context ?? CreateDetachedContext(session),
                    step,
                    DescribeDeviceFailure("The application could not be restarted", result),
                    result.ExitCode).ConfigureAwait(false);
                return;
            }

            session.LaunchTime = result.LaunchTime;
            session.State = RunSessionState.Running;
            session.EndedAt = null;
            session.Duration = null;
            session.Error = null;
            session.StatusMessage = "The application was restarted.";
            SucceedStep(session, step, "Restarted.");
            if (context is not null &&
                context.Request.Configuration.Logs.Attach &&
                session.LogcatSessionId is null)
            {
                await AttachLogcatAsync(context, cancellationToken).ConfigureAwait(false);
            }

            await SaveSnapshotAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            if (context is not null)
            {
                await FinishCancelledAsync(context).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<HotReloadResult?> HotReloadAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        await ReloadSessionAsync(sessionId, fullRestart: false, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<HotReloadResult?> HotRestartAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        await ReloadSessionAsync(sessionId, fullRestart: true, cancellationToken).ConfigureAwait(false);

    private Task<HotReloadResult?> ReloadSessionAsync(
        string sessionId,
        bool fullRestart,
        CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        return session is { IsReactNative: true }
            ? ReloadMetroAsync(sessionId, cancellationToken)
            : ReloadFlutterAsync(sessionId, fullRestart, cancellationToken);
    }

    /// <summary>
    /// Runs the guards shared by every reload path. Returns a failure result when the request
    /// cannot proceed, or null when the caller owns the session.
    /// </summary>
    private static HotReloadResult? CheckReloadRequest(
        RunSession session,
        RunContext? context,
        string featureName)
    {
        if (!session.SupportsHotReload)
        {
            return new HotReloadResult(
                false,
                $"Reload is only available for {featureName} debug run configurations.",
                TimeSpan.Zero);
        }

        if (context is null || session.IsReadOnly)
        {
            return new HotReloadResult(
                false,
                "The session is no longer active. Run the app again to reload it.",
                TimeSpan.Zero);
        }

        if (session.State is RunSessionState.Queued or RunSessionState.Preparing or RunSessionState.Building
            or RunSessionState.ResolvingArtifact or RunSessionState.Installing or RunSessionState.Launching)
        {
            return new HotReloadResult(
                false,
                "Wait for the current run to finish before reloading.",
                TimeSpan.Zero);
        }

        return null;
    }

    private async Task<HotReloadResult?> ReloadMetroAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var context = FindContext(sessionId);
        var session = context?.Session ?? GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        var guard = CheckReloadRequest(session, context, "React Native");
        if (guard is not null)
        {
            return guard;
        }

        var metro = context!.Metro;
        if (metro is null || !metro.IsRunning)
        {
            return new HotReloadResult(
                false,
                "Metro is not running for this session. Run the app again to start Metro, or apply the reload " +
                "manually by shaking the device and choosing Reload in the developer menu.",
                TimeSpan.Zero);
        }

        var step = AddStep(session, $"metro-reload-{Guid.NewGuid():N}", "Reload (Metro)", RunStepKind.HotReload);
        context.MetroStep = step;
        context.MetroTail.Clear();
        StartStep(session, step);
        session.State = RunSessionState.Running;
        session.EndedAt = null;
        session.Duration = null;
        session.StatusMessage = $"Reloading on {session.DeviceName ?? session.DeviceSerial} through Metro.";
        RaiseChanged(session);

        HotReloadResult result;
        try
        {
            result = await metro.ReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CancelStep(step);
            RaiseChanged(session);
            return new HotReloadResult(false, "The request was cancelled.", TimeSpan.Zero);
        }

        step.CommandLine = $"POST http://127.0.0.1:{metro.Port}/reload";
        step.OutputTail = JoinTail(context.MetroTail);
        if (result.Succeeded)
        {
            SucceedStep(session, step, result.Message);
            session.StatusMessage = result.Message;
        }
        else
        {
            FailStepOnly(session, step, result.Message);
            session.StatusMessage = result.Message;
            _notifications.Notify(new AppNotification
            {
                Event = NotificationEvent.RunFailed,
                Title = "Reload failed",
                Message = result.Message,
                SessionId = session.Id,
                NavigationTarget = "session",
            });
        }

        RaiseChanged(session);
        await SaveSnapshotAsync(session).ConfigureAwait(false);
        return result;
    }

    private async Task<HotReloadResult?> ReloadFlutterAsync(
        string sessionId,
        bool fullRestart,
        CancellationToken cancellationToken)
    {
        var context = FindContext(sessionId);
        var session = context?.Session ?? GetSession(sessionId);
        if (session is null)
        {
            return null;
        }

        var guard = CheckReloadRequest(session, context, "Flutter");
        if (guard is not null)
        {
            return guard;
        }

        if (string.IsNullOrWhiteSpace(session.DeviceSerial))
        {
            return new HotReloadResult(
                false,
                "The device is unknown. Run the app again with a selected device.",
                TimeSpan.Zero);
        }

        FlutterRunSession? flutter;
        try
        {
            flutter = context!.Flutter;
            if (flutter is null || !flutter.IsRunning)
            {
                flutter = await AttachFlutterAsync(context, cancellationToken).ConfigureAwait(false);
                if (flutter is null)
                {
                    return null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new HotReloadResult(false, "The request was cancelled.", TimeSpan.Zero);
        }

        var verb = fullRestart ? "Hot restart" : "Hot reload";
        var step = AddStep(
            session,
            $"hot-{(fullRestart ? "restart" : "reload")}-{Guid.NewGuid():N}",
            verb,
            RunStepKind.HotReload);
        context.FlutterStep = step;
        context.FlutterTail.Clear();
        StartStep(session, step);
        session.State = RunSessionState.Running;
        session.EndedAt = null;
        session.Duration = null;
        session.StatusMessage = $"{verb} in progress on {session.DeviceName ?? session.DeviceSerial}.";
        RaiseChanged(session);

        HotReloadResult result;
        try
        {
            result = fullRestart
                ? await flutter.HotRestartAsync(cancellationToken).ConfigureAwait(false)
                : await flutter.HotReloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            result = new HotReloadResult(
                false,
                $"The Flutter session stopped: {exception.Message}",
                TimeSpan.Zero);
            if (context.Flutter is { } dead)
            {
                context.Flutter = null;
                await dead.DisposeAsync().ConfigureAwait(false);
            }

            flutter = null;
        }
        catch (OperationCanceledException)
        {
            CancelStep(step);
            RaiseChanged(session);
            return new HotReloadResult(false, "The request was cancelled.", TimeSpan.Zero);
        }

        step.CommandLine = $"flutter {(fullRestart ? "hot restart" : "hot reload")}";
        step.OutputTail = JoinTail(context.FlutterTail);
        if (result.Succeeded)
        {
            SucceedStep(session, step, result.Message);
            session.StatusMessage = result.Message;
        }
        else
        {
            FailStepOnly(session, step, result.Message);
            session.StatusMessage = result.Message;
            if (flutter is null)
            {
                lock (session)
                {
                    session.State = RunSessionState.Failed;
                    session.Error = result.Message;
                    CompleteSession(session);
                }
            }

            _notifications.Notify(new AppNotification
            {
                Event = NotificationEvent.RunFailed,
                Title = $"{verb} failed",
                Message = result.Message,
                SessionId = session.Id,
                NavigationTarget = "session",
            });
        }

        RaiseChanged(session);
        await SaveSnapshotAsync(session).ConfigureAwait(false);
        return result;
    }

    private async Task<FlutterRunSession?> AttachFlutterAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var request = _planBuilder.CreateFlutterRunRequest(
            context.Request.Project,
            context.Request.Configuration,
            session.DeviceSerial);
        if (request is null)
        {
            session.StatusMessage = "Select a device before attaching a Flutter session.";
            RaiseChanged(session);
            return null;
        }

        var step = AddStep(session, $"flutter-attach-{Guid.NewGuid():N}", "Attach Flutter session", RunStepKind.HotReload);
        context.FlutterStep = step;
        context.FlutterTail.Clear();
        session.State = RunSessionState.Launching;
        session.StatusMessage =
            "Starting an attached Flutter session. The app is rebuilt, installed, and launched on the device.";
        StartStep(session, step);

        if (context.Flutter is { } previous)
        {
            context.Flutter = null;
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        var flutter = new FlutterRunSession(request, line => OnFlutterOutput(context, line));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.Cancellation.Token);
        try
        {
            await flutter.StartAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            await flutter.DisposeAsync().ConfigureAwait(false);
            context.FlutterStep = step;
            step.OutputTail = JoinTail(context.FlutterTail);
            await FailStepAsync(context, step, exception.Message).ConfigureAwait(false);
            session.State = RunSessionState.Failed;
            session.StatusMessage = "The Flutter session could not be attached.";
            RaiseChanged(session);
            await SaveSnapshotAsync(session).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            await flutter.DisposeAsync().ConfigureAwait(false);
            CancelStep(step);
            RaiseChanged(session);
            throw;
        }

        if (linked.IsCancellationRequested || session.IsFinished)
        {
            await flutter.DisposeAsync().ConfigureAwait(false);
            CancelStep(step);
            RaiseChanged(session);
            throw new OperationCanceledException(linked.Token);
        }

        step.CommandLine = request.ToCommandLine();
        step.OutputTail = JoinTail(context.FlutterTail);
        context.Flutter = flutter;
        SucceedStep(session, step, $"Flutter session attached to {flutter.AppId ?? "the app"}.");
        session.State = RunSessionState.Running;
        session.StatusMessage = "The Flutter session is attached. Hot reload and hot restart are available.";
        RaiseChanged(session);
        await SaveSnapshotAsync(session).ConfigureAwait(false);
        return flutter;
    }

    private void OnFlutterOutput(RunContext context, ProcessOutputLine line)
    {
        var step = context.FlutterStep;
        OnProcessOutput(
            context.Session,
            step?.Id ?? "flutter",
            line,
            context.FlutterTail,
            appendToBuildOutput: false);
    }

    private async Task StopFlutterAsync(RunContext context)
    {
        var flutter = context.Flutter;
        context.Flutter = null;
        if (flutter is null)
        {
            return;
        }

        try
        {
            await flutter.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            _logger.LogWarning(
                exception,
                "The Flutter session for run {SessionId} could not be stopped cleanly.",
                context.Session.Id);
        }
        finally
        {
            await flutter.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<RunSession> ApplyRecoveryAsync(
        string sessionId,
        RecoveryActionKind action,
        CancellationToken cancellationToken = default)
    {
        var context = FindContext(sessionId);
        var session = context?.Session ?? GetSession(sessionId)
            ?? throw new InvalidOperationException($"Run session \"{sessionId}\" was not found.");
        if (session.IsReadOnly)
        {
            return session;
        }

        var configuration = context?.Request.Configuration ?? new RunConfiguration();
        var step = AddStep(session, $"recovery-{Guid.NewGuid():N}", $"Recovery: {action}", RunStepKind.Install);
        if (action == RecoveryActionKind.EnableInstallViaUsb)
        {
            SkipStep(
                session,
                step,
                "Enable \"Install via USB\" in the device's Developer options, then run the app again.");
            session.StatusMessage = "Enable \"Install via USB\" in Developer options on the device, then retry.";
            RaiseChanged(session);
            return session;
        }

        var files = ResolveArtifactFiles(session);
        if (files.Count == 0)
        {
            await FailStepAsync(
                context ?? CreateDetachedContext(session),
                step,
                "No APK is available for the recovery install. Run the app again so the artifact can be rebuilt.")
                .ConfigureAwait(false);
            return session;
        }

        if (string.IsNullOrWhiteSpace(session.DeviceSerial))
        {
            await FailStepAsync(
                context ?? CreateDetachedContext(session),
                step,
                "The device is unknown. Run the app again with a selected device.").ConfigureAwait(false);
            return session;
        }

        SetSessionState(session, RunSessionState.Installing, $"Applying recovery: {action}.");
        StartStep(session, step);
        var options = new AdbInstallOptions
        {
            Replace = configuration.Install.Replace,
            TestOnly = configuration.Install.TestOnly || action == RecoveryActionKind.RetryWithTestFlag,
            GrantPermissions = configuration.Install.GrantPermissions,
            AllowDowngrade = configuration.Install.AllowDowngrade || action == RecoveryActionKind.RetryWithDowngrade,
            User = configuration.Install.User,
            ExtraArguments = configuration.Install.ExtraArguments,
        };
        var tail = new Queue<string>();
        var progress = new ActionProgress<ProcessOutputLine>(
            line => OnProcessOutput(session, step.Id, line, tail, appendToBuildOutput: false));
        try
        {
            await using var deviceLease = await AcquireDeviceLockAsync(
                session.DeviceSerial,
                $"Recover {session.ProjectName} on {session.DeviceSerial}",
                alreadyHeld: false,
                cancellationToken).ConfigureAwait(false);
            if (action == RecoveryActionKind.UninstallAndReinstall)
            {
                var uninstall = await _commands
                    .UninstallAsync(session.DeviceSerial, session.PackageName ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
                tail.Enqueue(uninstall.Output);
            }

            if (action == RecoveryActionKind.FreeStorage)
            {
                var free = await _commands.GetFreeStorageAsync(session.DeviceSerial, cancellationToken).ConfigureAwait(false);
                tail.Enqueue(free is null
                    ? "The free space on /data could not be determined."
                    : $"Free space on /data: {FormatBytes(free.Value)}.");
            }

            var result = await _commands
                .InstallAsync(session.DeviceSerial, files, options, progress, cancellationToken)
                .ConfigureAwait(false);
            step.CommandLine = result.CommandLine;
            step.ExitCode = result.ExitCode;
            step.OutputTail = JoinTail(tail);
            if (!result.Succeeded)
            {
                var diagnosis = InstallFailureDiagnoser.Diagnose(result.Output + Environment.NewLine + result.Error);
                step.Diagnosis = diagnosis;
                step.RecoveryActions = diagnosis.Actions;
                NotifyInstallFailure(session, diagnosis);
                await FailStepAsync(
                    context ?? CreateDetachedContext(session),
                    step,
                    $"{diagnosis.Title}: {diagnosis.Explanation}",
                    result.ExitCode).ConfigureAwait(false);
                return session;
            }

            SucceedStep(session, step, $"Installed {files.Count} APK file(s).", result.ExitCode);
            session.State = RunSessionState.Running;
            session.EndedAt = null;
            session.Duration = null;
            session.Error = null;
            session.StatusMessage = "The install succeeded after recovery.";
            RaiseChanged(session);
            if (context is not null &&
                configuration.Logs.Attach &&
                session.LogcatSessionId is null)
            {
                await AttachLogcatAsync(context, cancellationToken).ConfigureAwait(false);
            }

            await SaveSnapshotAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            if (context is not null)
            {
                await FinishCancelledAsync(context).ConfigureAwait(false);
            }
        }

        return session;
    }

    /// <inheritdoc />
    public void RemoveSession(string sessionId)
    {
        RunContext? context;
        lock (_sync)
        {
            _sessions.RemoveAll(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
            _contexts.Remove(sessionId, out context);
        }

        context?.Cancellation.Cancel();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RunContext[] contexts;
        lock (_sync)
        {
            contexts = _contexts.Values.ToArray();
            _contexts.Clear();
            _sessions.Clear();
        }

        foreach (var context in contexts)
        {
            context.Cancellation.Cancel();
            try
            {
                await StopFlutterAsync(context).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The Flutter session already shut down.
            }

            try
            {
                await StopMetroAsync(context).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The Metro session already shut down.
            }

            try
            {
                await DetachLogcatAsync(context).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The logcat service already shut down.
            }

            context.Cancellation.Dispose();
        }

        MetroLease[] leases;
        lock (_sync)
        {
            leases = [.. _metroByPort.Values];
            _metroByPort.Clear();
        }

        foreach (var lease in leases)
        {
            try
            {
                await lease.Session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                _logger.LogWarning(exception, "A Metro instance could not be stopped during disposal.");
            }
        }
    }

    private static RunSession CreateSession(RunRequest request)
    {
        var serial = request.Device?.Serial
            ?? request.DeviceSerial
            ?? request.Configuration.DeviceSerial
            ?? string.Empty;
        return new RunSession
        {
            ProjectId = request.Project.Id,
            ProjectName = request.Project.Name,
            ProjectRoot = request.Project.RootPath,
            ConfigurationId = request.Configuration.Id,
            ConfigurationName = request.Configuration.Name,
            ConfigurationKind = request.Configuration.Kind,
            DeviceSerial = serial,
            DeviceName = request.Device?.Model ?? request.Device?.Serial,
            GroupId = request.GroupId,
            IsDryRun = request.DryRun,
            SupportsHotReload = (request.Configuration.Kind is RunConfigurationKind.Flutter
                                    or RunConfigurationKind.ReactNative)
                                && IsDebugVariant(request.Configuration.VariantName),
            State = RunSessionState.Queued,
            StatusMessage = request.DryRun ? "Dry run queued." : "Queued.",
        };
    }

    private static bool IsDebugVariant(string? variant) =>
        string.IsNullOrWhiteSpace(variant) ||
        string.Equals(variant.Trim(), "debug", StringComparison.OrdinalIgnoreCase);

    private void CompleteDryRun(RunSession session, RunRequest request)
    {
        var preview = _planBuilder.BuildPreview(request.Project, request.Configuration, session.DeviceSerial);
        var index = 0;
        foreach (var previewStep in preview.Steps)
        {
            session.Steps.Add(new RunStep
            {
                Id = $"dry-run-{index++}",
                Name = previewStep.Name,
                Kind = previewStep.Kind,
                CommandLine = previewStep.CommandLine,
                State = RunStepState.Skipped,
                SkipReason = previewStep.WillRun
                    ? "Dry run: this command was not executed."
                    : previewStep.SkipReason ?? "The step would be skipped.",
            });
        }

        session.State = RunSessionState.Succeeded;
        session.StatusMessage = preview.IsValid
            ? $"Dry run: previewed {preview.Steps.Count} step(s) without executing anything."
            : $"Dry run found {preview.Errors.Count} configuration problem(s).";
        if (!preview.IsValid)
        {
            session.State = RunSessionState.Failed;
            session.Error = string.Join(
                Environment.NewLine,
                preview.Errors.Select(error => $"{error.Field}: {error.Message}"));
        }

        CompleteSession(session);
        RaiseChanged(session);
    }

    private async Task ExecuteAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var request = context.Request;
        var project = request.Project;
        var configuration = request.Configuration;

        await using var deviceLease = await AcquireDeviceLockAsync(
            session.DeviceSerial,
            $"Run {project.Name} on {session.DeviceSerial}",
            request.Options.DeviceLockHeld,
            cancellationToken).ConfigureAwait(false);

        var prepare = AddStep(session, "prepare", "Prepare", RunStepKind.WaitForDevice);
        SetSessionState(session, RunSessionState.Preparing, "Checking the run configuration and device.");
        StartStep(session, prepare);
        var validation = RunConfigurationValidator.Validate(project, configuration);
        if (!validation.IsValid)
        {
            var message = string.Join(
                Environment.NewLine,
                validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            await FailStepAsync(context, prepare, message).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(session.DeviceSerial))
        {
            await FailStepAsync(
                context,
                prepare,
                "Select a device before running the project.").ConfigureAwait(false);
            return;
        }

        if (!Directory.Exists(project.RootPath))
        {
            await FailStepAsync(
                context,
                prepare,
                $"The project folder does not exist: {project.RootPath}").ConfigureAwait(false);
            return;
        }

        if (configuration.Kind == RunConfigurationKind.ReactNative &&
            !Directory.Exists(Path.Combine(project.RootPath, "android")))
        {
            await FailStepAsync(
                context,
                prepare,
                "This Expo project has no android folder yet, so it cannot be built or installed. " +
                "Run \"npx expo prebuild --platform android\" in the project, then refresh the project in DroidDeck.")
                .ConfigureAwait(false);
            return;
        }

        SucceedStep(session, prepare, "The configuration and device are valid.");

        if (!await RunBuildAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await ResolveArtifactAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await InstallAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await PrepareMetroAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await LaunchAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var customResult = await RunCustomStepsAsync(context, cancellationToken).ConfigureAwait(false);
        if (customResult == CustomStepResult.Terminal)
        {
            return;
        }

        await AttachLogcatAsync(context, cancellationToken).ConfigureAwait(false);
        if (customResult == CustomStepResult.PartiallyFailed)
        {
            lock (session)
            {
                session.State = RunSessionState.PartiallyFailed;
                session.Error ??= "One or more custom steps failed.";
                session.StatusMessage = context.LaunchWasSkipped
                    ? "The run steps completed, but some failed."
                    : "The app is running, but some custom steps failed.";
                CompleteSession(session);
            }

            RaiseChanged(session);
            await SaveAndNotifyAsync(session).ConfigureAwait(false);
            return;
        }

        if (context.LaunchWasSkipped)
        {
            await StopMetroAsync(context).ConfigureAwait(false);
            lock (session)
            {
                session.State = RunSessionState.Succeeded;
                session.StatusMessage = "The run steps completed.";
                CompleteSession(session);
            }

            RaiseChanged(session);
            await SaveAndNotifyAsync(session).ConfigureAwait(false);
            return;
        }

        lock (session)
        {
            session.State = RunSessionState.Running;
            session.StatusMessage = $"The app is running on {session.DeviceName ?? session.DeviceSerial}.";
        }

        RaiseChanged(session);
        await SaveSnapshotAsync(session).ConfigureAwait(false);
    }

    private async Task<bool> RunBuildAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var configuration = context.Request.Configuration;
        var step = AddStep(session, "build", "Build", RunStepKind.Build);
        var isBuildable = configuration.Kind
            is RunConfigurationKind.Gradle
            or RunConfigurationKind.Flutter
            or RunConfigurationKind.ReactNative;
        if (!isBuildable ||
            configuration.Build.Skip ||
            context.Request.Options.SkipBuild ||
            context.Request.Options.PrebuiltArtifactFiles is { Count: > 0 })
        {
            SkipStep(
                session,
                step,
                !isBuildable
                    ? "Only Gradle, Flutter, and React Native projects have a build step."
                    : context.Request.Options.PrebuiltArtifactFiles is { Count: > 0 }
                        ? "The artifact was already built for this device group."
                        : "The build is disabled for this run.");
            return true;
        }

        var buildRequest = _planBuilder.CreateBuildRequest(context.Request.Project, configuration);
        if (buildRequest is null)
        {
            SkipStep(session, step, "No build command was produced for this configuration.");
            return true;
        }

        var buildLabel = configuration.Kind == RunConfigurationKind.Flutter
            ? $"the Flutter {configuration.VariantName ?? "debug"} app"
            : configuration.Build.Task;
        SetSessionState(session, RunSessionState.Building, $"Building {buildLabel}.");
        step.CommandLine = buildRequest.ToCommandLine();
        StartStep(session, step);
        var tail = new Queue<string>();
        var progress = new ActionProgress<ProcessOutputLine>(
            line => OnProcessOutput(session, step.Id, line, tail, appendToBuildOutput: true));
        ProcessResult result;
        IAsyncDisposable? resourceLease = null;
        try
        {
            if (_resourceGuard is not null)
            {
                resourceLease = await _resourceGuard
                    .AcquireAsync(
                        ResourceKind.Build,
                        $"{session.ProjectName} on {session.DeviceName ?? session.DeviceSerial}",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            result = await _runner.RunAsync(buildRequest, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            await FinishCancelledAsync(context).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (resourceLease is not null)
            {
                await resourceLease.DisposeAsync().ConfigureAwait(false);
            }
        }

        step.ExitCode = result.ExitCode;
        step.OutputTail = JoinTail(tail);
        if (result.WasCanceled)
        {
            CancelStep(step);
            await FinishCancelledAsync(context).ConfigureAwait(false);
            return false;
        }

        if (result.StartError is not null)
        {
            await FailStepAsync(
                context,
                step,
                $"The build could not start: {result.StartError}",
                result.ExitCode).ConfigureAwait(false);
            return false;
        }

        if (result.TimedOut)
        {
            await FailStepAsync(
                context,
                step,
                $"The build timed out after {configuration.Build.TimeoutSeconds} seconds.",
                result.ExitCode).ConfigureAwait(false);
            return false;
        }

        if (!result.Succeeded)
        {
            var errors = BuildErrorParser.Parse(result.CombinedOutput, session.ProjectRoot);
            step.Errors = [.. errors];
            var message = errors.Count > 0
                ? $"The build failed with {errors.Count} error(s). First: {errors[0].Display}"
                : $"The build failed with exit code {result.ExitCode}.";
            await FailStepAsync(context, step, message, result.ExitCode).ConfigureAwait(false);
            return false;
        }

        SucceedStep(
            session,
            step,
            $"Build finished in {result.Duration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s.",
            result.ExitCode);
        return true;
    }

    private async Task<bool> ResolveArtifactAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var step = AddStep(session, "artifact", "Locate artifact", RunStepKind.Install);
        if (!IsArtifactPlanned(context.Request))
        {
            SkipStep(session, step, "No artifact glob is configured for this project.");
            return true;
        }

        SetSessionState(session, RunSessionState.ResolvingArtifact, "Locating the build artifact.");
        StartStep(session, step);
        var prebuilt = context.Request.Options.PrebuiltArtifactFiles;
        if (prebuilt is { Count: > 0 })
        {
            session.ArtifactFiles = [.. prebuilt];
            session.ArtifactPath = context.Request.Options.PrebuiltArtifactPath ?? prebuilt[0];
            session.PackageName ??= context.Request.Options.PrebuiltPackageName;
            session.Activity ??= context.Request.Options.PrebuiltActivity;
            step.CommandLine = context.Request.Options.PrebuiltArtifactDescription
                ?? "Reusing the artifact built for this device group.";
            SucceedStep(
                session,
                step,
                prebuilt.Count == 1
                    ? "Reused the APK built for this group."
                    : $"Reused the base APK and {prebuilt.Count - 1} split APK(s) built for this group.");
            return true;
        }

        ArtifactResolution resolution;
        try
        {
            resolution = await _artifacts
                .ResolveAsync(context.Request.Project, context.Request.Configuration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            await FinishCancelledAsync(context).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await FailStepAsync(context, step, exception.Message).ConfigureAwait(false);
            return false;
        }

        step.CommandLine = resolution.Glob;
        if (!resolution.Succeeded)
        {
            var message = resolution.Error ?? "The artifact could not be resolved.";
            if (resolution.Candidates.Count > 1)
            {
                message += Environment.NewLine +
                           "Candidates:" + Environment.NewLine +
                           string.Join(Environment.NewLine, resolution.Candidates.Select(candidate => "  " + candidate));
            }

            await FailStepAsync(context, step, message).ConfigureAwait(false);
            return false;
        }

        session.ArtifactPath = resolution.BaseApk;
        session.ArtifactFiles = [.. resolution.Files];
        session.PackageName ??= resolution.Info?.PackageName;
        session.Activity ??= resolution.Info?.LaunchableActivity;
        SucceedStep(
            session,
            step,
            resolution.HasSplits
                ? $"Found the base APK and {resolution.Files.Count - 1} split APK(s)."
                : "Found the APK.");
        return true;
    }

    private async Task<bool> InstallAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var configuration = context.Request.Configuration;
        var step = AddStep(session, "install", "Install", RunStepKind.Install);
        if (!IsArtifactPlanned(context.Request))
        {
            SkipStep(session, step, "No artifact is configured for this project.");
            return true;
        }

        if (context.Request.Options.SkipInstall ||
            configuration.Install.ExtraArguments.Contains("--no-install", StringComparer.Ordinal))
        {
            SkipStep(session, step, "The install is disabled for this run.");
            return true;
        }

        var files = ResolveArtifactFiles(session);
        if (files.Count == 0)
        {
            await FailStepAsync(
                context,
                step,
                "No APK is available to install. Build the project or point the artifact glob at an APK.")
                .ConfigureAwait(false);
            return false;
        }

        SetSessionState(
            session,
            RunSessionState.Installing,
            files.Count == 1
                ? $"Installing {Path.GetFileName(files[0])}."
                : $"Installing {files.Count} APK files.");
        StartStep(session, step);
        var tail = new Queue<string>();
        var progress = new ActionProgress<ProcessOutputLine>(
            line => OnProcessOutput(session, step.Id, line, tail, appendToBuildOutput: false));
        try
        {
            if (context.Request.Options.UninstallFirst && !string.IsNullOrWhiteSpace(session.PackageName))
            {
                var uninstall = await _commands
                    .UninstallAsync(session.DeviceSerial, session.PackageName, cancellationToken)
                    .ConfigureAwait(false);
                tail.Enqueue(uninstall.Output);
            }

            var options = new AdbInstallOptions
            {
                Replace = configuration.Install.Replace,
                TestOnly = configuration.Install.TestOnly && context.Request.Options.AllowTestOnly,
                GrantPermissions = configuration.Install.GrantPermissions,
                AllowDowngrade = configuration.Install.AllowDowngrade || context.Request.Options.AllowDowngrade,
                User = configuration.Install.User,
                ExtraArguments = configuration.Install.ExtraArguments,
            };
            var result = await _commands
                .InstallAsync(session.DeviceSerial, files, options, progress, cancellationToken)
                .ConfigureAwait(false);
            step.CommandLine = result.CommandLine;
            step.ExitCode = result.ExitCode;
            step.OutputTail = JoinTail(tail);
            if (result.WasCanceled)
            {
                CancelStep(step);
                await FinishCancelledAsync(context).ConfigureAwait(false);
                return false;
            }

            if (!result.Succeeded)
            {
                var diagnosis = InstallFailureDiagnoser.Diagnose(result.Output + Environment.NewLine + result.Error);
                step.Diagnosis = diagnosis;
                step.RecoveryActions = diagnosis.Actions;
                NotifyInstallFailure(session, diagnosis);
                await FailStepAsync(
                    context,
                    step,
                    $"{diagnosis.Title}: {diagnosis.Explanation}",
                    result.ExitCode).ConfigureAwait(false);
                return false;
            }

            SucceedStep(
                session,
                step,
                files.Count == 1 ? "Installed the APK." : $"Installed {files.Count} APK files.",
                result.ExitCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            await FinishCancelledAsync(context).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> PrepareMetroAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var request = context.Request;
        var configuration = request.Configuration;
        if (configuration.Kind != RunConfigurationKind.ReactNative || !IsDebugVariant(configuration.VariantName))
        {
            // Release builds bundle the JavaScript into the APK; no development server is needed.
            return true;
        }

        var info = request.Project.ReactNativeInfo;
        var port = info?.MetroPort is > 0 and <= 65535 ? info.MetroPort : ReactNativeProjectInfo.DefaultMetroPort;
        var metroRequest = _planBuilder.CreateMetroStartRequest(request.Project, configuration);
        if (metroRequest is null)
        {
            return true;
        }

        var step = AddStep(session, "metro", "Start Metro", RunStepKind.Metro);
        context.MetroStep = step;
        context.MetroTail.Clear();
        SetSessionState(session, RunSessionState.Preparing, "Preparing the Metro development server.");
        StartStep(session, step);
        step.CommandLine = metroRequest.ToCommandLine();

        MetroSession? metro;
        lock (_sync)
        {
            if (_metroByPort.TryGetValue(port, out var lease) && lease.Session.IsRunning)
            {
                lease.Owners.Add(session.Id);
                metro = lease.Session;
            }
            else
            {
                metro = null;
            }
        }

        if (metro is null)
        {
            var created = new MetroSession(
                metroRequest,
                port,
                line => OnMetroOutput(context, line),
                probe: _metroProbe,
                reload: _metroReload);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                context.Cancellation.Token);
            try
            {
                await created.StartAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await created.DisposeAsync().ConfigureAwait(false);
                CancelStep(step);
                await FinishCancelledAsync(context).ConfigureAwait(false);
                return false;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or TimeoutException or Win32Exception)
            {
                await created.DisposeAsync().ConfigureAwait(false);
                step.OutputTail = JoinTail(context.MetroTail);
                await FailStepAsync(context, step, exception.Message).ConfigureAwait(false);
                return false;
            }

            lock (_sync)
            {
                if (_metroByPort.TryGetValue(port, out var existing) && existing.Session.IsRunning)
                {
                    // Another session won the race for the port; share its instance instead.
                    existing.Owners.Add(session.Id);
                    metro = existing.Session;
                }
                else
                {
                    // A reused external server is never owned, so it is never stopped with the session.
                    _metroByPort[port] = new MetroLease(created, !created.ReusedExisting, [session.Id]);
                    metro = created;
                }
            }

            if (!ReferenceEquals(metro, created))
            {
                await created.DisposeAsync().ConfigureAwait(false);
                SucceedStep(session, step, $"Reusing the Metro instance already running on port {port}.");
            }
            else if (created.ReusedExisting)
            {
                SucceedStep(session, step, $"Reusing the Metro instance already running on port {port}.");
            }
            else
            {
                SucceedStep(session, step, $"Metro is running on port {port}.");
            }
        }
        else
        {
            SucceedStep(session, step, $"Reusing the Metro instance already running on port {port}.");
        }

        context.Metro = metro;

        var reverse = AddStep(session, "reverse", "Map device port", RunStepKind.Metro);
        SetSessionState(session, RunSessionState.Preparing, $"Mapping port {port} on the device to Metro.");
        StartStep(session, reverse);
        var reverseResult = await _commands
            .ReverseAsync(session.DeviceSerial, port, port, cancellationToken)
            .ConfigureAwait(false);
        reverse.CommandLine = reverseResult.CommandLine;
        reverse.ExitCode = reverseResult.ExitCode;
        if (!reverseResult.Succeeded)
        {
            await FailStepAsync(
                context,
                reverse,
                $"The device could not map port {port} to Metro with adb reverse. " +
                $"Reconnect the device and run the app again, or apply the tunnel manually with " +
                $"\"adb -s {session.DeviceSerial} reverse tcp:{port} tcp:{port}\".")
                .ConfigureAwait(false);
            return false;
        }

        SucceedStep(session, reverse, $"Port {port} on the device maps to Metro on this computer.");
        return true;
    }

    private void OnMetroOutput(RunContext context, ProcessOutputLine line)
    {
        var step = context.MetroStep;
        OnProcessOutput(
            context.Session,
            step?.Id ?? "metro",
            line,
            context.MetroTail,
            appendToBuildOutput: false);
    }

    private async Task StopMetroAsync(RunContext context)
    {
        var metro = context.Metro;
        context.Metro = null;
        if (metro is null)
        {
            return;
        }

        var shouldStop = true;
        lock (_sync)
        {
            if (_metroByPort.TryGetValue(metro.Port, out var lease) &&
                ReferenceEquals(lease.Session, metro))
            {
                lease.Owners.Remove(context.Session.Id);
                shouldStop = lease.Owners.Count == 0 && lease.Owned;
                if (lease.Owners.Count == 0)
                {
                    _metroByPort.Remove(metro.Port);
                }
            }
        }

        if (shouldStop)
        {
            try
            {
                await metro.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                _logger.LogWarning(
                    exception,
                    "The Metro instance for run {SessionId} could not be stopped cleanly.",
                    context.Session.Id);
            }

            await metro.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> LaunchAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var configuration = context.Request.Configuration;
        var step = AddStep(session, "launch", "Launch", RunStepKind.Launch);
        if (context.Request.Options.SkipLaunch)
        {
            SkipStep(session, step, "The launch is disabled for this run.");
            return true;
        }

        var template = CreateTemplateContext(context);
        var packageName = TemplateEngine.Expand(
            configuration.Launch.PackageName ?? session.PackageName ?? context.Request.Project.ApplicationId,
            template);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            if (configuration.Kind == RunConfigurationKind.Custom)
            {
                context.LaunchWasSkipped = true;
                SkipStep(session, step, "No package name is configured for this project.");
                return true;
            }

            await FailStepAsync(
                context,
                step,
                "The package name is unknown. Set the launch package name in the run configuration.")
                .ConfigureAwait(false);
            return false;
        }

        var activity = TemplateEngine.Expand(
            configuration.Launch.Activity ?? session.Activity ?? context.Request.Project.LauncherActivity,
            template);
        SetSessionState(session, RunSessionState.Launching, $"Launching {packageName}.");
        StartStep(session, step);
        try
        {
            if (string.IsNullOrWhiteSpace(activity))
            {
                activity = await _commands.ResolveActivityAsync(session.DeviceSerial, packageName, cancellationToken)
                    .ConfigureAwait(false) ?? string.Empty;
            }

            if (configuration.Launch.StopBeforeLaunch)
            {
                await _commands.ForceStopAsync(session.DeviceSerial, packageName, cancellationToken).ConfigureAwait(false);
            }

            if (configuration.Logs.Attach && configuration.Logs.ClearBeforeLaunch)
            {
                await _commands.ClearLogcatAsync(session.DeviceSerial, cancellationToken).ConfigureAwait(false);
            }

            var result = await _commands
                .LaunchAsync(
                    session.DeviceSerial,
                    new AdbLaunchOptions
                    {
                        PackageName = packageName,
                        Activity = string.IsNullOrWhiteSpace(activity) ? null : activity,
                        Action = TemplateEngine.Expand(configuration.Launch.Action, template),
                        Data = TemplateEngine.Expand(configuration.Launch.Data, template),
                        ExtraArguments = TemplateEngine.Expand(configuration.Launch.ExtraArguments, template),
                        Wait = configuration.Launch.Wait,
                        WaitForDebugger = configuration.Launch.WaitForDebugger,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            step.CommandLine = result.CommandLine;
            step.ExitCode = result.ExitCode;
            step.OutputTail = Tail(result.Output);
            if (!result.Succeeded)
            {
                step.RecoveryActions = [RetryAction("launch-retry", "Retry launch", "Launches the app again.")];
                await FailStepAsync(
                    context,
                    step,
                    DescribeDeviceFailure("The application could not be launched", result),
                    result.ExitCode).ConfigureAwait(false);
                return false;
            }

            session.PackageName = packageName;
            session.Activity = string.IsNullOrWhiteSpace(activity) ? session.Activity : activity;
            session.LaunchTime = result.LaunchTime;
            SucceedStep(
                session,
                step,
                result.LaunchTime is { } launchTime
                    ? $"Launched in {launchTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms."
                    : "Launched.",
                result.ExitCode);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            await FinishCancelledAsync(context).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<CustomStepResult> RunCustomStepsAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var configuration = context.Request.Configuration;
        var template = CreateTemplateContext(context);
        var partiallyFailed = false;
        var index = 0;
        foreach (var definition in configuration.Steps)
        {
            var step = AddStep(
                session,
                $"custom-{index++}-{SafeStepId(definition.Id)}",
                string.IsNullOrWhiteSpace(definition.Name) ? definition.Id : definition.Name,
                definition.Kind);
            if (!definition.Enabled)
            {
                SkipStep(session, step, "The step is disabled.");
                continue;
            }

            var shouldRun = TemplateEngine.EvaluateCondition(definition.Condition, template, out var reason);
            if (!shouldRun)
            {
                SkipStep(session, step, reason ?? "The step condition is false.");
                continue;
            }

            var command = TemplateEngine.Expand(definition.Command, template);
            if (string.IsNullOrWhiteSpace(command))
            {
                var failure = $"Step \"{step.Name}\" has no command.";
                if (definition.FailurePolicy == StepFailurePolicy.Continue)
                {
                    FailStepOnly(session, step, failure);
                    partiallyFailed = true;
                    continue;
                }

                await FailStepAsync(context, step, failure).ConfigureAwait(false);
                return CustomStepResult.Terminal;
            }

            var arguments = TemplateEngine.Expand(definition.Arguments, template);
            var workingDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory)
                ? session.ProjectRoot
                : Path.IsPathRooted(definition.WorkingDirectory)
                    ? definition.WorkingDirectory
                    : Path.Combine(session.ProjectRoot, definition.WorkingDirectory);
            if (!Directory.Exists(workingDirectory))
            {
                var failure = $"The working directory does not exist: {workingDirectory}";
                if (definition.FailurePolicy == StepFailurePolicy.Continue)
                {
                    FailStepOnly(session, step, failure);
                    partiallyFailed = true;
                    continue;
                }

                await FailStepAsync(context, step, failure).ConfigureAwait(false);
                return CustomStepResult.Terminal;
            }

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in definition.Environment)
            {
                environment[key] = TemplateEngine.Expand(value, template);
            }

            var request = new ProcessRequest
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Environment = environment,
                Timeout = TimeSpan.FromSeconds(Math.Clamp(definition.TimeoutSeconds, 1, 24 * 60 * 60)),
            };
            step.CommandLine = request.ToCommandLine();
            var attempts = definition.FailurePolicy == StepFailurePolicy.Retry
                ? Math.Max(1, definition.RetryCount + 1)
                : 1;
            var tail = new Queue<string>();
            var progress = new ActionProgress<ProcessOutputLine>(
                line => OnProcessOutput(session, step.Id, line, tail, appendToBuildOutput: false));
            ProcessResult? last = null;
            var succeeded = false;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                StartStep(session, step);
                try
                {
                    last = await _runner.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancelStep(step);
                    await FinishCancelledAsync(context).ConfigureAwait(false);
                    return CustomStepResult.Terminal;
                }

                if (last.WasCanceled)
                {
                    CancelStep(step);
                    await FinishCancelledAsync(context).ConfigureAwait(false);
                    return CustomStepResult.Terminal;
                }

                step.ExitCode = last.ExitCode;
                step.OutputTail = JoinTail(tail);
                if (last.Succeeded)
                {
                    succeeded = true;
                    break;
                }

                if (attempt < attempts)
                {
                    step.Error = $"Attempt {attempt} failed with exit code {last.ExitCode}. Retrying.";
                }
            }

            if (succeeded)
            {
                SucceedStep(session, step, "Completed.", last?.ExitCode);
                continue;
            }

            var message = last is null
                ? "The step did not produce a result."
                : last.TimedOut
                    ? $"The step timed out after {definition.TimeoutSeconds} seconds."
                    : last.StartError is not null
                        ? $"The step could not start: {last.StartError}"
                        : $"The step failed with exit code {last.ExitCode}.";
            if (definition.FailurePolicy == StepFailurePolicy.Continue)
            {
                FailStepOnly(session, step, message, last?.ExitCode);
                partiallyFailed = true;
                continue;
            }

            await FailStepAsync(context, step, message, last?.ExitCode).ConfigureAwait(false);
            return CustomStepResult.Terminal;
        }

        return partiallyFailed ? CustomStepResult.PartiallyFailed : CustomStepResult.Succeeded;
    }

    private async Task AttachLogcatAsync(RunContext context, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var configuration = context.Request.Configuration;
        var step = AddStep(session, $"logcat-{Guid.NewGuid():N}", "Attach logcat", RunStepKind.Logcat);
        if (!context.Request.Options.AttachLogs || !configuration.Logs.Attach)
        {
            SkipStep(session, step, "Logcat is disabled for this run.");
            return;
        }

        StartStep(session, step);
        if (string.IsNullOrWhiteSpace(session.PackageName))
        {
            SkipStep(session, step, "No package is associated with this run; logcat is not attached.");
            return;
        }

        try
        {
            var uid = await _commands.GetPackageUidAsync(
                session.DeviceSerial,
                session.PackageName ?? string.Empty,
                cancellationToken).ConfigureAwait(false);
            session.AppUid = uid;
            var request = new LogcatRequest
            {
                DeviceSerial = session.DeviceSerial,
                PackageName = session.PackageName,
                AppUid = uid,
                Buffers = configuration.Logs.Buffers,
                ClearBeforeAttach = false,
                AppOnly = configuration.Logs.AppOnly,
                MaxLines = configuration.Logs.MaxLines,
            };
            var logcat = await _logcat.StartAsync(request, cancellationToken).ConfigureAwait(false);
            context.Logcat = logcat;
            context.EntriesHandler = (_, args) => OnEntriesAdded(context, args);
            context.CrashHandler = (_, args) => OnCrashDetected(context, args);
            logcat.EntriesAdded += context.EntriesHandler;
            logcat.CrashDetected += context.CrashHandler;
            session.LogcatSessionId = logcat.Id;
            step.CommandLine = BuildLogcatCommandLine(request);
            SucceedStep(session, step, "Logcat is attached.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelStep(step);
            await FinishCancelledAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            FailStepOnly(session, step, $"Logcat could not be attached: {exception.Message}");
            session.StatusMessage = $"The app is running, but logcat could not be attached: {exception.Message}";
            RaiseChanged(session);
        }
    }

    private void OnProcessOutput(
        RunSession session,
        string stepId,
        ProcessOutputLine line,
        Queue<string> tail,
        bool appendToBuildOutput)
    {
        lock (session)
        {
            if (appendToBuildOutput)
            {
                if (session.BuildOutput.Count >= MaxBuildOutputLines)
                {
                    session.BuildOutput.RemoveRange(0, 10_000);
                }

                session.BuildOutput.Add(line.Text);
            }

            tail.Enqueue(line.Text);
            while (tail.Count > MaxStepOutputTailLines)
            {
                tail.Dequeue();
            }
        }

        OutputReceived?.Invoke(
            this,
            new RunOutputEventArgs(
                session.Id,
                stepId,
                line.Text,
                line.Stream == ProcessOutputChannel.StandardError));
    }

    private void OnEntriesAdded(RunContext context, LogEntriesEventArgs args)
    {
        var session = context.Session;
        var max = Math.Clamp(
            context.Request.Configuration.Logs.MaxLines,
            100,
            MaxLogEntries);
        lock (session)
        {
            session.LogEntries.AddRange(args.Entries);
            var overflow = session.LogEntries.Count - max;
            if (overflow > 0)
            {
                session.LogEntries.RemoveRange(0, overflow);
            }
        }

        LogEntriesReceived?.Invoke(this, args);
    }

    private void OnCrashDetected(RunContext context, CrashDetectedEventArgs args)
    {
        var session = context.Session;
        lock (session)
        {
            // The logcat pipeline's CrashDetector already dedupes echo reports of the
            // same crash within its window, so every event raised here is distinct.
            session.Crashes.Add(args.Crash);
        }

        _notifications.Notify(new AppNotification
        {
            Event = NotificationEvent.Crash,
            Title = args.Crash.Kind switch
            {
                CrashKind.Anr => "Application not responding",
                CrashKind.Native => "Native crash detected",
                _ => "Crash detected",
            },
            Message = args.Crash.Summary,
            SessionId = session.Id,
            NavigationTarget = "session",
        });
        CrashDetected?.Invoke(this, args);
        RaiseChanged(session);
    }

    private async Task DetachLogcatAsync(RunContext context)
    {
        var logcat = context.Logcat;
        context.Logcat = null;
        if (logcat is null)
        {
            return;
        }

        if (context.EntriesHandler is not null)
        {
            logcat.EntriesAdded -= context.EntriesHandler;
            context.EntriesHandler = null;
        }

        if (context.CrashHandler is not null)
        {
            logcat.CrashDetected -= context.CrashHandler;
            context.CrashHandler = null;
        }

        try
        {
            await _logcat.StopAsync(logcat.Id).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(exception, "The logcat stream for session {SessionId} did not stop cleanly.", context.Session.Id);
        }

        context.Session.LogcatSessionId = null;
    }

    private async Task FinishCancelledAsync(RunContext context)
    {
        await StopMetroAsync(context).ConfigureAwait(false);
        var session = context.Session;
        var changed = false;
        lock (session)
        {
            if (!session.IsFinished)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var step in session.Steps)
                {
                    if (step.State == RunStepState.Running)
                    {
                        step.State = RunStepState.Cancelled;
                        step.EndedAt = now;
                        step.Duration = step.StartedAt is { } started ? now - started : null;
                    }
                }

                session.State = RunSessionState.Cancelled;
                session.Error = context.StoppedByUser ? "The run was stopped by the user." : "The run was cancelled.";
                session.StatusMessage = session.Error;
                CompleteSession(session);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        RaiseChanged(session);
        await SaveAndNotifyAsync(session).ConfigureAwait(false);
    }

    private async Task FailSessionAsync(RunContext context, string message)
    {
        await StopMetroAsync(context).ConfigureAwait(false);
        var session = context.Session;
        lock (session)
        {
            if (session.IsFinished)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var step in session.Steps)
            {
                if (step.State == RunStepState.Running)
                {
                    step.State = RunStepState.Failed;
                    step.EndedAt = now;
                    step.Duration = step.StartedAt is { } started ? now - started : null;
                    step.Error ??= message;
                }
            }

            session.State = RunSessionState.Failed;
            session.Error = message;
            session.StatusMessage = message;
            CompleteSession(session);
        }

        RaiseChanged(session);
        await SaveAndNotifyAsync(session).ConfigureAwait(false);
    }

    private async Task FailStepAsync(
        RunContext context,
        RunStep step,
        string message,
        int? exitCode = null)
    {
        await StopMetroAsync(context).ConfigureAwait(false);
        var session = context.Session;
        lock (session)
        {
            var now = DateTimeOffset.UtcNow;
            step.State = RunStepState.Failed;
            step.EndedAt = now;
            step.Duration = step.StartedAt is { } started ? now - started : null;
            step.Error = message;
            if (exitCode is not null)
            {
                step.ExitCode = exitCode;
            }

            session.State = RunSessionState.Failed;
            session.Error = message;
            session.StatusMessage = message;
            CompleteSession(session);
        }

        RaiseChanged(session);
        await SaveAndNotifyAsync(session).ConfigureAwait(false);
    }

    private void FailStepOnly(RunSession session, RunStep step, string message, int? exitCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        step.State = RunStepState.Failed;
        step.EndedAt = now;
        step.Duration = step.StartedAt is { } started ? now - started : null;
        step.Error = message;
        if (exitCode is not null)
        {
            step.ExitCode = exitCode;
        }

        RaiseChanged(session);
    }

    private async Task SaveAndNotifyAsync(RunSession session)
    {
        await SaveSnapshotAsync(session).ConfigureAwait(false);
        _notifications.Notify(NotificationService.ForSession(session));
    }

    private async Task SaveSnapshotAsync(RunSession session)
    {
        try
        {
            RunSession snapshot;
            lock (session)
            {
                snapshot = DroidDeckJson.Deserialize<RunSession>(DroidDeckJson.Serialize(session)) ?? session;
            }

            await _sessionStore.SaveAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Run session {SessionId} could not be saved.", session.Id);
        }
    }

    private void NotifyInstallFailure(RunSession session, InstallDiagnosis diagnosis)
    {
        _notifications.Notify(new AppNotification
        {
            Event = NotificationEvent.InstallFailed,
            Title = diagnosis.Title,
            Message = diagnosis.Explanation,
            SessionId = session.Id,
            NavigationTarget = "session",
        });
    }

    private static List<string> ResolveArtifactFiles(RunSession session)
    {
        if (session.ArtifactFiles.Count > 0)
        {
            return session.ArtifactFiles;
        }

        return string.IsNullOrWhiteSpace(session.ArtifactPath) ? [] : [session.ArtifactPath];
    }

    private static bool IsArtifactPlanned(RunRequest request)
    {
        return request.Options.PrebuiltArtifactFiles is { Count: > 0 } ||
               request.Configuration.Kind == RunConfigurationKind.Gradle ||
               !string.IsNullOrWhiteSpace(request.Configuration.Artifact.Glob) ||
               request.Project.ApkFiles.Count > 0;
    }

    private static RunTemplateContext CreateTemplateContext(RunContext context)
    {
        var session = context.Session;
        var template = RunTemplateContext.ForProject(context.Request.Project, context.Request.Configuration);
        template.DeviceSerial = session.DeviceSerial;
        template.DeviceModel = context.Request.Device?.Model;
        template.DeviceApiLevel = context.Request.Device?.ApiLevel;
        template.IsEmulator = context.Request.Device?.IsEmulator
            ?? session.DeviceSerial.StartsWith("emulator-", StringComparison.Ordinal);
        template.ApkPath = session.ArtifactPath;
        template.Activity = session.Activity;
        return template;
    }

    private static RecoveryAction RetryAction(string id, string label, string description) => new()
    {
        Id = id,
        Label = label,
        Description = description,
        Kind = RecoveryActionKind.Retry,
    };

    private static string DescribeDeviceFailure(string prefix, DeviceCommandResult result)
    {
        if (result.StartError is not null)
        {
            return $"{prefix}: {result.StartError}";
        }

        if (result.TimedOut)
        {
            return $"{prefix}: the device did not respond in time.";
        }

        var detail = FirstMeaningfulLine(result.Output);
        return string.IsNullOrWhiteSpace(detail)
            ? $"{prefix} (exit code {result.ExitCode})."
            : $"{prefix}: {detail}";
    }

    private static string? FirstMeaningfulLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string Tail(string output)
    {
        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .TakeLast(MaxStepOutputTailLines)
            .ToArray();
        return string.Join(Environment.NewLine, lines);
    }

    private static string? JoinTail(IEnumerable<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines);
        return text.Length == 0 ? null : text;
    }

    private string BuildLogcatCommandLine(LogcatRequest request)
    {
        var arguments = new List<string>
        {
            "-s", request.DeviceSerial, "logcat",
            "-v", "threadtime",
            "-v", "year",
            "-v", "uid",
            "-T", "1",
        };
        foreach (var buffer in request.Buffers)
        {
            arguments.Add("-b");
            arguments.Add(buffer);
        }

        return ProcessRequest.FormatCommandLine(_commands.AdbExecutable, arguments);
    }

    private static string FormatBytes(long bytes)
    {
        const double kilobyte = 1024;
        const double megabyte = kilobyte * 1024;
        const double gigabyte = megabyte * 1024;
        return bytes switch
        {
            >= (long)gigabyte => $"{bytes / gigabyte:F2} GB",
            >= (long)megabyte => $"{bytes / megabyte:F1} MB",
            >= (long)kilobyte => $"{bytes / kilobyte:F0} KB",
            _ => $"{bytes} B",
        };
    }

    private static string SafeStepId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "step";
        }

        var safe = new string(id.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray());
        return safe.Trim('-');
    }

    private static RunStep AddStep(RunSession session, string id, string name, RunStepKind kind)
    {
        var step = new RunStep
        {
            Id = id,
            Name = name,
            Kind = kind,
            State = RunStepState.Pending,
        };
        lock (session)
        {
            session.Steps.Add(step);
        }

        return step;
    }

    private static void SetSessionState(RunSession session, RunSessionState state, string message)
    {
        session.State = state;
        session.StatusMessage = message;
    }

    private void StartStep(RunSession session, RunStep step)
    {
        step.State = RunStepState.Running;
        step.StartedAt = DateTimeOffset.UtcNow;
        RaiseChanged(session);
    }

    private void SucceedStep(RunSession session, RunStep step, string? message = null, int? exitCode = null)
    {
        var now = DateTimeOffset.UtcNow;
        step.State = RunStepState.Succeeded;
        step.EndedAt = now;
        step.Duration = step.StartedAt is { } started ? now - started : null;
        if (exitCode is not null)
        {
            step.ExitCode = exitCode;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            step.OutputTail = string.IsNullOrWhiteSpace(step.OutputTail)
                ? message
                : step.OutputTail + Environment.NewLine + message;
        }

        RaiseChanged(session);
    }

    private void SkipStep(RunSession session, RunStep step, string reason)
    {
        step.State = RunStepState.Skipped;
        step.SkipReason = reason;
        step.EndedAt = DateTimeOffset.UtcNow;
        step.Duration = TimeSpan.Zero;
        RaiseChanged(session);
    }

    private static void CancelStep(RunStep step)
    {
        var now = DateTimeOffset.UtcNow;
        step.State = RunStepState.Cancelled;
        step.EndedAt = now;
        step.Duration = step.StartedAt is { } started ? now - started : null;
    }

    private static void CompleteSession(RunSession session)
    {
        var now = DateTimeOffset.UtcNow;
        session.EndedAt = now;
        session.Duration = now >= session.StartedAt ? now - session.StartedAt : TimeSpan.Zero;
    }

    private void RaiseChanged(RunSession session) =>
        SessionChanged?.Invoke(this, new RunSessionChangedEventArgs(session));

    private RunContext? FindContext(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_sync)
        {
            return _contexts.GetValueOrDefault(sessionId);
        }
    }

    private async Task<IAsyncDisposable?> AcquireDeviceLockAsync(
        string? serial,
        string operation,
        bool alreadyHeld,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null || alreadyHeld || string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }

        return await _coordinator.AcquireAsync(serial, operation, cancellationToken).ConfigureAwait(false);
    }

    private static RunContext CreateDetachedContext(RunSession session) =>
        new(session, new RunRequest
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

    private sealed class RunContext
    {
        public RunContext(RunSession session, RunRequest request)
        {
            Session = session;
            Request = request;
        }

        public RunSession Session { get; }

        public RunRequest Request { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public CancellationTokenSource? Linked { get; set; }

        public ILogcatSession? Logcat { get; set; }

        public FlutterRunSession? Flutter { get; set; }

        public RunStep? FlutterStep { get; set; }

        public Queue<string> FlutterTail { get; } = new();

        public MetroSession? Metro { get; set; }

        public RunStep? MetroStep { get; set; }

        public Queue<string> MetroTail { get; } = new();

        public EventHandler<LogEntriesEventArgs>? EntriesHandler { get; set; }

        public EventHandler<CrashDetectedEventArgs>? CrashHandler { get; set; }

        public bool StoppedByUser { get; set; }

        public bool LaunchWasSkipped { get; set; }
    }

    private enum CustomStepResult
    {
        Succeeded,
        PartiallyFailed,
        Terminal,
    }

    private sealed class MetroLease
    {
        public MetroLease(MetroSession session, bool owned, HashSet<string> owners)
        {
            Session = session;
            Owned = owned;
            Owners = owners;
        }

        public MetroSession Session { get; }

        public bool Owned { get; }

        public HashSet<string> Owners { get; }
    }

    private sealed class ActionProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;

        public ActionProgress(Action<T> action) => _action = action;

        public void Report(T value) => _action(value);
    }
}
