using System.Globalization;
using System.Text.Json.Serialization;

namespace DroidDeck.Core.Testing;

/// <summary>The outcome of one test case.</summary>
public enum TestStatus
{
    /// <summary>The test passed.</summary>
    Passed,

    /// <summary>The test failed an assertion.</summary>
    Failed,

    /// <summary>The test could not run to a result.</summary>
    Errored,

    /// <summary>The test was skipped or ignored.</summary>
    Skipped,
}

/// <summary>The structured formats DroidDeck can read.</summary>
public enum TestResultFormat
{
    /// <summary>JUnit XML produced by Gradle, custom runners, and most CI tools.</summary>
    JUnitXml,

    /// <summary>The JSON reporter emitted by <c>flutter test</c>.</summary>
    FlutterJson,

    /// <summary>The JSON reporter emitted by Jest.</summary>
    JestJson,

    /// <summary>The TRX file produced by <c>dotnet test</c>.</summary>
    TrxXml,
}

/// <summary>One artifact attached to a test case.</summary>
public sealed class TestAttachment
{
    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = "attachment";

    /// <summary>Gets or sets the absolute or relative file path, when the attachment is a file.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets the media type, when known.</summary>
    public string? MediaType { get; set; }

    /// <summary>Gets or sets inline content, when the attachment is text.</summary>
    public string? Content { get; set; }

    /// <summary>Gets whether the attachment points at a file.</summary>
    [JsonIgnore]
    public bool IsFile => !string.IsNullOrWhiteSpace(Path);
}

/// <summary>One parsed test case.</summary>
public sealed class TestCaseResult
{
    /// <summary>Gets or sets the test name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the declaring class or fixture.</summary>
    public string? ClassName { get; set; }

    /// <summary>Gets or sets the owning suite name.</summary>
    public string? SuiteName { get; set; }

    /// <summary>Gets or sets the outcome.</summary>
    public TestStatus Status { get; set; }

    /// <summary>Gets or sets how long the test took.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets or sets the short failure message.</summary>
    public string? Message { get; set; }

    /// <summary>Gets or sets the failure stack trace.</summary>
    public string? StackTrace { get; set; }

    /// <summary>Gets or sets the source file, when the runner reports one.</summary>
    public string? FilePath { get; set; }

    /// <summary>Gets or sets the source line, when the runner reports one.</summary>
    public int? Line { get; set; }

    /// <summary>Gets or sets the attachments.</summary>
    public List<TestAttachment> Attachments { get; set; } = [];

    /// <summary>Gets the fully qualified name used by rerun filters.</summary>
    [JsonIgnore]
    public string FullyQualifiedName => string.IsNullOrWhiteSpace(ClassName)
        ? Name
        : $"{ClassName}.{Name}";

    /// <summary>Gets whether the case did not pass.</summary>
    [JsonIgnore]
    public bool IsFailure => Status is TestStatus.Failed or TestStatus.Errored;
}

/// <summary>One parsed test suite.</summary>
public sealed class TestSuiteResult
{
    /// <summary>Gets or sets the suite name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets when the suite started, when the runner reports a timestamp.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>Gets or sets how long the suite took.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Gets or sets the cases in file order.</summary>
    public List<TestCaseResult> Cases { get; set; } = [];

    /// <summary>Gets the number of passed cases.</summary>
    [JsonIgnore]
    public int PassedCount => Cases.Count(test => test.Status == TestStatus.Passed);

    /// <summary>Gets the number of failed cases.</summary>
    [JsonIgnore]
    public int FailedCount => Cases.Count(test => test.Status == TestStatus.Failed);

    /// <summary>Gets the number of errored cases.</summary>
    [JsonIgnore]
    public int ErroredCount => Cases.Count(test => test.Status == TestStatus.Errored);

    /// <summary>Gets the number of skipped cases.</summary>
    [JsonIgnore]
    public int SkippedCount => Cases.Count(test => test.Status == TestStatus.Skipped);

    /// <summary>Gets the aggregate suite status.</summary>
    [JsonIgnore]
    public TestStatus Status => ErroredCount > 0
        ? TestStatus.Errored
        : FailedCount > 0
            ? TestStatus.Failed
            : Cases.Count > 0 && SkippedCount == Cases.Count
                ? TestStatus.Skipped
                : TestStatus.Passed;
}

/// <summary>Raised when a result file cannot be parsed.</summary>
public sealed class TestResultParseException : Exception
{
    /// <summary>Creates a parse failure for a source file.</summary>
    public TestResultParseException(string message, string? sourcePath = null, Exception? innerException = null)
        : base(Format(message, sourcePath), innerException)
    {
        SourcePath = sourcePath;
    }

    /// <summary>Gets the file that could not be parsed.</summary>
    public string? SourcePath { get; }

    private static string Format(string message, string? sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath)
            ? message
            : $"{message} ({sourcePath})";
}

/// <summary>Reads structured test results into suites and cases.</summary>
public static partial class TestResultParser
{
    /// <summary>Parses a result payload and returns every suite it contains.</summary>
    /// <param name="format">The structured format.</param>
    /// <param name="content">The file or process output contents.</param>
    /// <param name="sourcePath">The result file, used for errors and relative attachments.</param>
    public static IReadOnlyList<TestSuiteResult> Parse(
        TestResultFormat format,
        string content,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return format switch
        {
            TestResultFormat.JUnitXml => ParseJUnit(content, sourcePath),
            TestResultFormat.FlutterJson => ParseFlutterJson(content, sourcePath),
            TestResultFormat.JestJson => ParseJestJson(content, sourcePath),
            TestResultFormat.TrxXml => ParseTrxXml(content, sourcePath),
            _ => throw new TestResultParseException($"The test result format {format} is not supported yet.", sourcePath),
        };
    }

    internal static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            seconds < 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
