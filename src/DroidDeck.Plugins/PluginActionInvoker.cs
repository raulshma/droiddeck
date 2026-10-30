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
    /// only the wait is time-boxed.
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
        using var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            var content = await createView(linked.Token)
                .WaitAsync(_timeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            _health.RecordSuccess(pluginId);
            return new PluginViewResult(content, null, false, false, stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            return FailView(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            return FailView(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (Exception exception)
        {
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

    /// <summary>Invokes a plugin callback with timeout and failure isolation.</summary>
    public async Task<PluginInvocationResult> InvokeAsync(
        string pluginId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(operation);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCancellation = new CancellationTokenSource(_timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            // Run on the pool so a plugin that blocks before its first await cannot freeze the caller.
            var task = Task.Run(() => operation(linked.Token), linked.Token);
            await task.WaitAsync(_timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _health.RecordSuccess(pluginId);
            return PluginInvocationResult.Success(stopwatch.Elapsed);
        }
        catch (TimeoutException)
        {
            return Fail(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new PluginInvocationResult(false, false, true, "The plugin call was canceled.", stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return Fail(pluginId, "The plugin did not complete before its timeout.", timedOut: true);
        }
        catch (Exception exception)
        {
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
