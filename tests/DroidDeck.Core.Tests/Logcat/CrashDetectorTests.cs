using DroidDeck.Core.Logs;

namespace DroidDeck.Core.Tests.Logcat;

public sealed class CrashDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Process_DetectsAmCrashUsingAospFieldOrder()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var entry = Entry(
            "am_crash",
            "[1000,1234,\"com.example\",0,\"java.lang.IllegalStateException\",\"boom\",\"Main.kt\",42]",
            sequence: 7);

        var crash = detector.Process(entry);

        Assert.NotNull(crash);
        Assert.Equal(CrashKind.Crash, crash.Kind);
        Assert.Equal("com.example", crash.PackageName);
        Assert.Equal(1234, crash.Pid);
        Assert.Equal("java.lang.IllegalStateException: boom", crash.Summary);
        Assert.Contains("Main.kt:42", crash.StackTrace);
        Assert.Equal(7, crash.FirstSequence);
        Assert.Same(crash, detector.ActiveCrash);
    }

    [Fact]
    public void Process_DetectsAmAnrUsingAospFieldOrder()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var entry = Entry(
            "am_anr",
            "[1000,1234,\"com.example\",\"Input dispatching timed out\",0]",
            sequence: 3);

        var crash = detector.Process(entry);

        Assert.NotNull(crash);
        Assert.Equal(CrashKind.Anr, crash.Kind);
        Assert.Equal("com.example", crash.PackageName);
        Assert.Equal(1234, crash.Pid);
        Assert.Contains("Input dispatching timed out", crash.Summary);
    }

    [Fact]
    public void Process_IgnoresOtherPackagesWhenTargetIsSet()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var entry = Entry(
            "am_crash",
            "[1000,1234,\"com.other\",0,\"java.lang.RuntimeException\",\"boom\",\"Main.kt\",1]",
            sequence: 1);

        Assert.Null(detector.Process(entry));
        Assert.Null(detector.ActiveCrash);
    }

    [Fact]
    public void Process_DetectsJavaFatalExceptionAndCollectsStackLines()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var fatal = Entry(
            "AndroidRuntime",
            "FATAL EXCEPTION: main\nProcess: com.example, PID: 1234",
            sequence: 10,
            level: "E",
            pid: 1234);

        var crash = detector.Process(fatal);
        detector.Process(new LogEntry
        {
            Tag = "AndroidRuntime",
            Message = "java.lang.IllegalStateException: boom",
            Raw = "java.lang.IllegalStateException: boom",
            Pid = 1234,
            Sequence = 11,
        });
        detector.Process(new LogEntry
        {
            Tag = "AndroidRuntime",
            Message = "\tat com.example.MainActivity.onCreate(MainActivity.kt:12)",
            Raw = "\tat com.example.MainActivity.onCreate(MainActivity.kt:12)",
            Pid = 1234,
            Sequence = 12,
        });
        var completed = detector.Complete();

        Assert.NotNull(crash);
        Assert.Equal("com.example", crash.PackageName);
        Assert.Equal(1234, crash.Pid);
        Assert.Contains("FATAL EXCEPTION", crash.Summary);
        Assert.NotNull(completed);
        Assert.Contains("IllegalStateException", completed.StackTrace);
        Assert.Contains("MainActivity.kt:12", completed.StackTrace);
        Assert.Equal(12, completed.LastSequence);
    }

    [Fact]
    public void Process_DedupesJavaCrashAndFollowingAmCrash()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var fatal = Entry(
            "AndroidRuntime",
            "FATAL EXCEPTION: main\nProcess: com.example, PID: 1234",
            sequence: 20,
            level: "E",
            pid: 1234,
            timestamp: Now);
        var amCrash = Entry(
            "am_crash",
            "[1000,1234,\"com.example\",0,\"java.lang.IllegalStateException\",\"boom\",\"Main.kt\",42]",
            sequence: 21,
            timestamp: Now.AddSeconds(1));

        Assert.NotNull(detector.Process(fatal));
        Assert.Null(detector.Process(amCrash));
    }

    [Fact]
    public void Process_DetectsActivityManagerAnr()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var entry = Entry(
            "ActivityManager",
            "ANR in com.example (com.example/.MainActivity)",
            sequence: 30,
            level: "E");

        var crash = detector.Process(entry);

        Assert.NotNull(crash);
        Assert.Equal(CrashKind.Anr, crash.Kind);
        Assert.Equal("com.example", crash.PackageName);
    }

    [Fact]
    public void Process_DetectsNativeCrashMarker()
    {
        var detector = new CrashDetector { TargetPackage = "com.example" };
        var entry = Entry("DEBUG", "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***", sequence: 40);

        var crash = detector.Process(entry);

        Assert.NotNull(crash);
        Assert.Equal(CrashKind.Native, crash.Kind);
        Assert.Equal("com.example", crash.PackageName);
    }

    [Fact]
    public void Reset_ClearsActiveCrashAndDedupeState()
    {
        var detector = new CrashDetector();
        var entry = Entry(
            "am_crash",
            "[1000,1234,\"com.example\",0,\"java.lang.RuntimeException\",\"boom\",\"Main.kt\",1]",
            sequence: 50);

        Assert.NotNull(detector.Process(entry));
        detector.Reset();

        Assert.Null(detector.ActiveCrash);
        Assert.NotNull(detector.Process(entry));
    }

    private static LogEntry Entry(
        string tag,
        string message,
        long sequence,
        string level = "I",
        int? pid = null,
        DateTimeOffset? timestamp = null) => new()
        {
            Tag = tag,
            Message = message,
            Raw = $"{tag}: {message}",
            Sequence = sequence,
            Level = level,
            Pid = pid,
            Timestamp = timestamp ?? Now,
        };
}
