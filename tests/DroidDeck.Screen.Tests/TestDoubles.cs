using DroidDeck.Core.Processes;
using DroidDeck.Core.Screen;

namespace DroidDeck.Screen.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessRequest> _requests = [];

    public Func<ProcessRequest, ProcessResult>? Handler { get; set; }

    public IReadOnlyList<ProcessRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Task.FromResult(Handler?.Invoke(request) ?? Success(request));
    }

    public static ProcessResult Success(ProcessRequest request, string standardOutput = "") => new()
    {
        CommandLine = request.ToCommandLine(),
        ExitCode = 0,
        StandardOutput = standardOutput,
    };

    public static ProcessResult Failure(ProcessRequest request, string output) => new()
    {
        CommandLine = request.ToCommandLine(),
        ExitCode = 1,
        StandardError = output,
    };
}

internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly object _gate = new();
    private readonly List<ProcessRequest> _requests = [];
    private readonly List<int> _terminated = [];
    private readonly HashSet<int> _running = [];
    private int _nextProcessId = 1000;

    public IReadOnlyList<ProcessRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public IReadOnlyList<int> Terminated
    {
        get
        {
            lock (_gate)
            {
                return _terminated.ToArray();
            }
        }
    }

    public int StartFailuresRemaining { get; set; }

    public bool ExitImmediately { get; set; }

    public LaunchedProcess Start(ProcessRequest request)
    {
        lock (_gate)
        {
            if (StartFailuresRemaining > 0)
            {
                StartFailuresRemaining--;
                throw new System.ComponentModel.Win32Exception("The fake executable was not found.");
            }

            _requests.Add(request);
            var processId = _nextProcessId++;
            if (!ExitImmediately)
            {
                _running.Add(processId);
            }

            return new LaunchedProcess(processId, request.ToCommandLine());
        }
    }

    public void Terminate(int processId)
    {
        lock (_gate)
        {
            _terminated.Add(processId);
            _running.Remove(processId);
        }
    }

    public bool IsRunning(int processId)
    {
        lock (_gate)
        {
            return _running.Contains(processId);
        }
    }

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        lock (_gate)
        {
            return !_running.Contains(processId);
        }
    }

    public Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(WaitForExit(processId, timeout));

    public void Exit(int processId)
    {
        lock (_gate)
        {
            _running.Remove(processId);
        }
    }
}

internal sealed class FakeScreenWindowController : IScreenWindowController
{
    public bool MoveResult { get; set; } = true;

    public bool FocusResult { get; set; } = true;

    public bool CloseResult { get; set; }

    public int MoveFailuresRemaining { get; set; }

    public int FocusFailuresRemaining { get; set; }

    public Action<int>? OnClose { get; set; }

    public List<(int ProcessId, ScreenBounds Bounds)> Moves { get; } = [];

    public List<int> Focused { get; } = [];

    public List<int> HiddenProcesses { get; } = [];

    public bool TryMove(int processId, ScreenBounds bounds)
    {
        Moves.Add((processId, bounds));
        if (MoveFailuresRemaining > 0)
        {
            MoveFailuresRemaining--;
            return false;
        }

        return MoveResult;
    }

    public bool TryFocus(int processId)
    {
        Focused.Add(processId);
        if (FocusFailuresRemaining > 0)
        {
            FocusFailuresRemaining--;
            return false;
        }

        return FocusResult;
    }

    public bool TryHide(int processId)
    {
        HiddenProcesses.Add(processId);
        return true;
    }

    public bool TryClose(int processId)
    {
        if (!CloseResult)
        {
            return false;
        }

        OnClose?.Invoke(processId);
        return true;
    }
}

internal sealed class FakeScreenService : IScreenService
{
    public List<ScreenSessionInfo> Sessions { get; } = [];

    public IReadOnlyList<ScreenSessionInfo> ActiveSessions => Sessions;

    public bool IsAvailable => true;

    public string? AvailabilityError => null;

    public event EventHandler? SessionsChanged
    {
        add { }
        remove { }
    }

    public Task<ScreenOperationResult> StartAsync(
        string serial,
        ScreenStartOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ScreenOperationResult> StopAsync(string serial, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task StopAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ScreenOperationResult> UpdateBoundsAsync(
        string serial,
        ScreenBounds bounds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ScreenOperationResult> FocusAsync(string serial, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ScreenOperationResult> PressKeyAsync(
        string serial,
        DeviceControlKey key,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ScreenOperationResult> ScreenshotAsync(
        string serial,
        string? directory = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ScreenOperationResult> StartRecordingAsync(
        string serial,
        string? directory = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ScreenOperationResult> StopRecordingAsync(
        string serial,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
