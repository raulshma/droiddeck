using DroidDeck.Core.Logs;

namespace DroidDeck.Core.Runs;

internal sealed class RunContext
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
