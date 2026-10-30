using System.Diagnostics;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>The outcome of a plugin extension invocation.</summary>
/// <param name="Succeeded">Whether the extension completed successfully.</param>
/// <param name="TimedOut">Whether the invocation exceeded its timeout.</param>
/// <param name="Canceled">Whether the caller canceled the invocation.</param>
/// <param name="Error">The failure message, when the invocation failed.</param>
/// <param name="Duration">How long the invocation took.</param>
public sealed record PluginInvocationResult(
    bool Succeeded,
    bool TimedOut,
    bool Canceled,
    string? Error,
    TimeSpan Duration)
{
    /// <summary>Creates a successful result.</summary>
    public static PluginInvocationResult Success(TimeSpan duration) => new(true, false, false, null, duration);
}

/// <summary>
/// Runs plugin extension callbacks with a timeout, isolates their failures,
/// and records the outcome in a <see cref="PluginHealthTracker"/>.
/// </summary>
public sealed class PluginActionInvoker
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly PluginHealthTracker _health;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeout;
    private readonly ILogger<PluginActionInvoker> _logger;

    /// <summary>Creates an invoker.</summary>
    public PluginActionInvoker(
        PluginHealthTracker health,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null,
        ILogger<PluginActionInvoker>? logger = null)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The plugin timeout must be positive.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<PluginActionInvoker>.Instance;
    }

    /// <summary>Gets the effective timeout.</summary>
    public TimeSpan Timeout => _timeout;

    /// <summary>Invokes a device action with timeout and failure isolation.</summary>
    public Task<PluginInvocationResult> InvokeDeviceActionAsync(
        PluginExtension<IDeviceAction> extension,
        DeviceActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return InvokeDeviceActionAsync(extension.PluginId, extension.Value, context, cancellationToken);
    }

    /// <summary>Invokes a device action with timeout and failure isolation.</summary>
    public Task<PluginInvocationResult> InvokeDeviceActionAsync(
        string pluginId,
        IDeviceAction action,
        DeviceActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        return InvokeAsync(
            pluginId,
            token => action.ExecuteAsync(context, token),
            cancellationToken);
    }

    /// <summary>Invokes a toolbar action with timeout and failure isolation.</summary>
    public Task<PluginInvocationResult> InvokeToolbarActionAsync(
        PluginExtension<IDeviceToolbarAction> extension,
        DeviceActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return InvokeToolbarActionAsync(extension.PluginId, extension.Value, context, cancellationToken);
    }

    /// <summary>Invokes a toolbar action with timeout and failure isolation.</summary>
    public Task<PluginInvocationResult> InvokeToolbarActionAsync(
        string pluginId,
        IDeviceToolbarAction action,
        DeviceActionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(context);
        return InvokeAsync(
            pluginId,
            token => action.ExecuteAsync(context, token),
            cancellationToken);
    }

    /// <summary>Creates tab content with failure isolation. Timeouts do not apply because creation is synchronous.</summary>
    public PluginTabContentResult CreateTabContent(
        PluginExtension<IDeviceTabProvider> extension,
        DeviceTabContext context)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return CreateTabContent(extension.PluginId, extension.Value, context);
    }

    /// <summary>Creates tab content with failure isolation. Timeouts do not apply because creation is synchronous.</summary>
    public PluginTabContentResult CreateTabContent(
        string pluginId,
        IDeviceTabProvider tab,
        DeviceTabContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var content = tab.CreateContent(context);
            _health.RecordSuccess(pluginId);
            return new PluginTabContentResult(content, null);
        }
        catch (Exception exception)
        {
            _health.RecordFailure(pluginId, exception.Message);
            _logger.LogWarning(
                exception,
                "Plugin {PluginId} failed to create tab {TabId}.",
                pluginId,
                tab.Id);
            return new PluginTabContentResult(null, exception.Message);
        }
    }

    /// <summary>
    /// Creates a plugin view with timeout and failure isolation. The factory
    /// runs on the caller's thread so WinUI content keeps its thread affinity;
    /// only the wait is time-boxed. When the wait is given up, the abandoned
    /// factory keeps its cancellation token source until the task settles, so
    /// the token it was handed still works instead of throwing
    /// <see cref="ObjectDisposedException"/> inside plugin code.
    /// </summary>
    public async Task<PluginViewResult> CreateViewAsync(
        string pluginId,
        string operation,
        Func<CancellationToken, Task<object>> createView,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(createView);

        var stopwatch = Stopwatch.StartNew();
        var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        Task<object>? pluginTask = null;
        try
        {
            pluginTask = createView(linked.Token);
            var content = await pluginTask
                .WaitAsync(_timeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            DisposeTokens(linked, timeoutCancellation);
            _health.RecordSuccess(pluginId);
            return new PluginViewResult(content, null, false, false, stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            Abandon(pluginTask!, linked, timeoutCancellation);
            return FailView(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The wait was canceled by the caller, but the plugin task may
            // still run, so the token sources stay alive until it settles.
            Abandon(pluginTask!, linked, timeoutCancellation);
            stopwatch.Stop();
            return new PluginViewResult(
                null,
                "The plugin call was canceled.",
                false,
                true,
                stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            DisposeTokens(linked, timeoutCancellation);
            return FailView(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (Exception exception)
        {
            if (pluginTask is null || pluginTask.IsCompleted)
            {
                DisposeTokens(linked, timeoutCancellation);
            }

            return FailView(pluginId, exception.Message, timedOut: false);
        }

        PluginViewResult FailView(string id, string message, bool timedOut)
        {
            stopwatch.Stop();
            _health.RecordFailure(id, message, timedOut);
            _logger.LogWarning(
                "Plugin {PluginId} failed to create {Operation}.",
                id,
                operation);
            return new PluginViewResult(null, message, timedOut, false, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Delivers a session panel update on the caller's thread with failure
    /// isolation: a throwing panel is recorded unhealthy and returned as a
    /// failed result, so the host can fall back without disturbing siblings.
    /// Updates are synchronous UI-thread mutations, so no timeout applies.
    /// </summary>
    public PluginInvocationResult UpdateSessionPanel(
        string pluginId,
        ISessionPanel panel,
        SessionPanelSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(snapshot);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            panel.UpdateSession(snapshot);
            stopwatch.Stop();
            _health.RecordSuccess(pluginId);
            return PluginInvocationResult.Success(stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _health.RecordFailure(pluginId, exception.Message);
            _logger.LogWarning(
                exception,
                "Plugin {PluginId} failed to update session panel {PanelId}.",
                pluginId,
                panel.Id);
            return new PluginInvocationResult(false, false, false, exception.Message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Prepares a plugin dialog for display: the content is created with the
    /// usual timeout isolation, and the result carries the error so the host
    /// can show a fallback message instead of the plugin content.
    /// </summary>
    public Task<PluginDialogPresentation> CreateDialogAsync(
        PluginExtension<IPluginDialog> extension,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return CreateDialogAsync(extension.PluginId, extension.Value, cancellationToken);
    }

    /// <summary>
    /// Prepares a plugin dialog for display: the content is created with the
    /// usual timeout isolation, and the result carries the error so the host
    /// can show a fallback message instead of the plugin content.
    /// </summary>
    public async Task<PluginDialogPresentation> CreateDialogAsync(
        string pluginId,
        IPluginDialog dialog,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(dialog);
        var content = await CreateViewAsync(
            pluginId,
            $"dialog {dialog.Id}",
            token => dialog.CreateContentAsync(token),
            cancellationToken).ConfigureAwait(false);
        return new PluginDialogPresentation(pluginId, dialog, content);
    }

    /// <summary>
    /// Delivers the dialog outcome to the plugin with timeout and failure
    /// isolation; a throwing handler is recorded as a plugin fault.
    /// </summary>
    public Task<PluginInvocationResult> InvokeDialogClosedAsync(
        PluginExtension<IPluginDialog> extension,
        string? buttonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return InvokeDialogClosedAsync(extension.PluginId, extension.Value, buttonId, cancellationToken);
    }

    /// <summary>
    /// Delivers the dialog outcome to the plugin with timeout and failure
    /// isolation; a throwing handler is recorded as a plugin fault.
    /// </summary>
    public Task<PluginInvocationResult> InvokeDialogClosedAsync(
        string pluginId,
        IPluginDialog dialog,
        string? buttonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(dialog);
        return InvokeAsync(
            pluginId,
            token => dialog.OnClosedAsync(buttonId, token),
            cancellationToken);
    }

    /// <summary>
    /// Invokes a plugin callback that can block on the user, such as a status
    /// bar click that presents a modal dialog. The wait is deliberately not
    /// time-boxed: a dialog the user keeps open would otherwise surface as a
    /// false plugin timeout while it is still showing. Content creation inside
    /// any dialog stays inside the usual timeout (the dialog service creates
    /// it through <see cref="CreateDialogAsync"/>), exceptions are isolated as
    /// always, and a watchdog reports a health fault once the callback
    /// outlives the normal timeout so a stuck plugin stays visible to the
    /// crash guard.
    /// </summary>
    public async Task<PluginInvocationResult> InvokeUiCallbackAsync(
        string pluginId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(operation);

        var stopwatch = Stopwatch.StartNew();
        var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        var watchdogFired = 0;
        ITimer? watchdog = null;
        Task? pluginTask = null;
        try
        {
            // Run on the pool so a plugin that blocks before its first await cannot freeze the caller.
            pluginTask = Task.Run(() => operation(linked.Token), linked.Token);
            watchdog = _timeProvider.CreateTimer(
                _ =>
                {
                    if (Interlocked.Exchange(ref watchdogFired, 1) == 0)
                    {
                        _health.RecordFailure(
                            pluginId,
                            $"A user-facing callback is still waiting after its {_timeout.TotalSeconds:F0} " +
                                "second timeout; it may be showing a dialog.",
                            timedOut: true);
                    }
                },
                null,
                _timeout,
                System.Threading.Timeout.InfiniteTimeSpan);

            await pluginTask.ConfigureAwait(false);
            stopwatch.Stop();
            Interlocked.Exchange(ref watchdogFired, 1);
            DisposeTokens(linked, timeoutCancellation);
            _health.RecordSuccess(pluginId);
            return PluginInvocationResult.Success(stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Abandon(pluginTask!, linked, timeoutCancellation);
            stopwatch.Stop();
            return new PluginInvocationResult(false, false, true, "The plugin call was canceled.", stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            Interlocked.Exchange(ref watchdogFired, 1);
            if (pluginTask is null || pluginTask.IsCompleted)
            {
                DisposeTokens(linked, timeoutCancellation);
            }
            else
            {
                Abandon(pluginTask, linked, timeoutCancellation);
            }

            _health.RecordFailure(pluginId, exception.Message);
            _logger.LogWarning(
                "Plugin {PluginId} user callback failed: {Message}",
                pluginId,
                exception.Message);
            return new PluginInvocationResult(false, false, false, exception.Message, stopwatch.Elapsed);
        }
        finally
        {
            watchdog?.Dispose();
        }
    }

    /// <summary>Invokes a plugin callback with timeout and failure isolation.</summary>
    public async Task<PluginInvocationResult> InvokeAsync(
        string pluginId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(operation);

        var stopwatch = Stopwatch.StartNew();
        var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        Task? pluginTask = null;
        try
        {
            // Run on the pool so a plugin that blocks before its first await cannot freeze the caller.
            pluginTask = Task.Run(() => operation(linked.Token), linked.Token);
            await pluginTask.WaitAsync(_timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            DisposeTokens(linked, timeoutCancellation);
            _health.RecordSuccess(pluginId);
            return PluginInvocationResult.Success(stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            Abandon(pluginTask!, linked, timeoutCancellation);
            return Fail(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The wait was canceled by the caller, but the plugin task may
            // still run, so the token sources stay alive until it settles.
            Abandon(pluginTask!, linked, timeoutCancellation);
            stopwatch.Stop();
            return new PluginInvocationResult(false, false, true, "The plugin call was canceled.", stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            DisposeTokens(linked, timeoutCancellation);
            return Fail(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (Exception exception)
        {
            if (pluginTask is null || pluginTask.IsCompleted)
            {
                DisposeTokens(linked, timeoutCancellation);
            }

            return Fail(pluginId, exception.Message, timedOut: false);
        }

        PluginInvocationResult Fail(string id, string message, bool timedOut)
        {
            stopwatch.Stop();
            _health.RecordFailure(id, message, timedOut);
            _logger.LogWarning(
                "Plugin {PluginId} invocation failed ({TimedOut}): {Message}",
                id,
                timedOut,
                message);
            return new PluginInvocationResult(false, timedOut, false, message, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Keeps the abandoned plugin task's token sources alive until the task
    /// settles. Disposing them as soon as the wait ends would surface
    /// <see cref="ObjectDisposedException"/> inside plugin code that still uses
    /// the token it was handed, and would cut off the very cancellation meant
    /// to stop it. A late fault is observed and logged so it cannot surface as
    /// an unobserved task exception.
    /// </summary>
    private void Abandon(Task pluginTask, CancellationTokenSource linked, CancellationTokenSource timeoutCancellation)
    {
        pluginTask.ContinueWith(
            _ =>
            {
                if (pluginTask.IsFaulted && pluginTask.Exception is { } fault)
                {
                    _logger.LogWarning(fault, "An abandoned plugin task failed after its wait ended.");
                }

                DisposeTokens(linked, timeoutCancellation);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void DisposeTokens(CancellationTokenSource linked, CancellationTokenSource timeoutCancellation)
    {
        try
        {
            linked.Dispose();
        }
        finally
        {
            timeoutCancellation.Dispose();
        }
    }
}

/// <summary>The outcome of creating plugin tab content.</summary>
/// <param name="Content">The created content, or <see langword="null"/> when creation failed.</param>
/// <param name="Error">The failure message.</param>
public sealed record PluginTabContentResult(object? Content, string? Error)
{
    /// <summary>Gets whether content was created.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// The outcome of creating a plugin view for a page, project tab, or home
/// widget. The host shows the content when it succeeded and a fallback card
/// when it failed.
/// </summary>
/// <param name="Content">The created content, or <see langword="null"/> when creation failed.</param>
/// <param name="Error">The failure message.</param>
/// <param name="TimedOut">Whether the creation exceeded its timeout.</param>
/// <param name="Canceled">Whether the caller canceled the creation.</param>
/// <param name="Duration">How long the creation took.</param>
public sealed record PluginViewResult(
    object? Content,
    string? Error,
    bool TimedOut,
    bool Canceled,
    TimeSpan Duration)
{
    /// <summary>Gets whether content was created.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>
/// A plugin dialog prepared for host display. When the plugin content failed to
/// create, <see cref="Content"/> carries the error and the host shows its
/// fallback message instead of the plugin content.
/// </summary>
/// <param name="PluginId">The contributing plugin identifier.</param>
/// <param name="Dialog">The dialog contribution.</param>
/// <param name="Content">The isolated content-creation outcome.</param>
public sealed record PluginDialogPresentation(
    string PluginId,
    IPluginDialog Dialog,
    PluginViewResult Content)
{
    /// <summary>Gets whether the dialog content was created.</summary>
    public bool Succeeded => Content.Succeeded;
}
