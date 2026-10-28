using System.ComponentModel;
using System.Diagnostics;

namespace DroidDeck.Core.Processes;

/// <summary>A process that was started without waiting for it to exit.</summary>
/// <param name="ProcessId">The operating-system process identifier.</param>
/// <param name="CommandLine">The exact command line that was started.</param>
public sealed record LaunchedProcess(int ProcessId, string CommandLine);

/// <summary>
/// Starts long-running helper processes that must outlive the caller, such as the
/// Android emulator or scrcpy. Unlike <see cref="IProcessRunner"/> this does not
/// attach the process to a job object or wait for output.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>Starts a process without waiting for it to exit.</summary>
    LaunchedProcess Start(ProcessRequest request);

    /// <summary>Terminates a process tree that this launcher started, if it is still running.</summary>
    void Terminate(int processId);

    /// <summary>Returns whether a started process is still running.</summary>
    bool IsRunning(int processId);

    /// <summary>Waits up to <paramref name="timeout"/> for a process to exit.</summary>
    /// <returns><see langword="true"/> when the process exited within the timeout.</returns>
    bool WaitForExit(int processId, TimeSpan timeout);

    /// <summary>Waits asynchronously up to <paramref name="timeout"/> for a process to exit.</summary>
    /// <returns><see langword="true"/> when the process exited within the timeout.</returns>
    Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>Starts detached desktop processes with <see cref="Process"/>.</summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    /// <inheritdoc />
    public LaunchedProcess Start(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = request.CreateNoWindow,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in request.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new Win32Exception($"The process \"{request.FileName}\" could not be started.");
        return new LaunchedProcess(process.Id, request.ToCommandLine());
    }

    /// <inheritdoc />
    public void Terminate(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process already exited or cannot be controlled; there is nothing left to terminate.
        }
    }

    /// <inheritdoc />
    public bool IsRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0 || timeout <= TimeSpan.Zero)
        {
            return !IsRunning(processId);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(timeout);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            return true;
        }

        if (timeout <= TimeSpan.Zero)
        {
            return !IsRunning(processId);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !IsRunning(processId);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return true;
        }
    }
}
