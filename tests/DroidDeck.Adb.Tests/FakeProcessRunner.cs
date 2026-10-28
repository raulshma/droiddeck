using DroidDeck.Core.Processes;

namespace DroidDeck.Adb.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly List<ProcessRequest> _requests = [];

    public Func<ProcessRequest, ProcessResult>? Handler { get; set; }

    public Func<ProcessRequest, IReadOnlyList<ProcessOutputLine>>? OutputLines { get; set; }

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

        if (OutputLines is { } outputLines && output is not null)
        {
            foreach (var line in outputLines(request))
            {
                output.Report(line);
            }
        }

        return Task.FromResult(Handler?.Invoke(request) ?? Success(request));
    }

    public static ProcessResult Success(ProcessRequest request, string standardOutput = "") => new()
    {
        CommandLine = request.ToCommandLine(),
        ExitCode = 0,
        StandardOutput = standardOutput,
    };

    public static ProcessResult Failure(ProcessRequest request, string output, int exitCode = 1) => new()
    {
        CommandLine = request.ToCommandLine(),
        ExitCode = exitCode,
        StandardOutput = output,
    };

    public static bool HasArguments(ProcessRequest request, params string[] arguments) =>
        request.Arguments.Count >= arguments.Length &&
        arguments.Select((argument, index) => request.Arguments[index] == argument).All(static match => match);
}

internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly List<ProcessRequest> _requests = [];

    public IReadOnlyList<ProcessRequest> Requests => _requests;

    public List<int> Terminated { get; } = [];

    public int ProcessId { get; set; } = 4242;

    public LaunchedProcess Start(ProcessRequest request)
    {
        _requests.Add(request);
        return new LaunchedProcess(ProcessId, request.ToCommandLine());
    }

    public void Terminate(int processId) => Terminated.Add(processId);

    public bool IsRunning(int processId) => RunningProcessIds.Contains(processId);

    public HashSet<int> RunningProcessIds { get; } = [];

    public bool WaitForExit(int processId, TimeSpan timeout) => !RunningProcessIds.Contains(processId);

    public Task<bool> WaitForExitAsync(
        int processId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(WaitForExit(processId, timeout));
}

internal sealed class FakeWirelessDeviceStore : DroidDeck.Core.Wireless.IWirelessDeviceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DroidDeck.Core.Wireless.WirelessDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    public FakeWirelessDeviceStore(params DroidDeck.Core.Wireless.WirelessDevice[] devices)
    {
        foreach (var device in devices)
        {
            _devices[device.Address] = device;
        }
    }

    public Task<IReadOnlyList<DroidDeck.Core.Wireless.WirelessDevice>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<DroidDeck.Core.Wireless.WirelessDevice> snapshot = _devices.Values
                .OrderBy(static device => device.Address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    public Task SaveAsync(
        DroidDeck.Core.Wireless.WirelessDevice device,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _devices[device.Address] = device;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string address, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _devices.Remove(address);
        }

        return Task.CompletedTask;
    }
}
