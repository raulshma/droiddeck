using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DroidDeck.Core.Testing;

public static partial class TestResultParser
{
    private static List<TestSuiteResult> ParseJUnit(string content, string? sourcePath)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(content),
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new TestResultParseException(
                $"The JUnit XML could not be parsed: {exception.Message}",
                sourcePath,
                exception);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName is not ("testsuites" or "testsuite"))
        {
            throw new TestResultParseException(
                "The XML does not contain a JUnit <testsuite> or <testsuites> element.",
                sourcePath);
        }

        var suiteElements = root
            .DescendantsAndSelf()
            .Where(element => element.Name.LocalName == "testsuite")
            .ToArray();
        if (suiteElements.Length == 0)
        {
            return [];
        }

        var fallbackName = string.IsNullOrWhiteSpace(sourcePath)
            ? "Tests"
            : Path.GetFileNameWithoutExtension(sourcePath);
        var suites = new List<TestSuiteResult>(suiteElements.Length);
        foreach (var element in suiteElements)
        {
            var testCaseElements = element
                .Elements()
                .Where(child => child.Name.LocalName == "testcase")
                .ToArray();
            if (testCaseElements.Length == 0 && element.Elements().Any(child => child.Name.LocalName == "testsuite"))
            {
                continue;
            }

            var suite = new TestSuiteResult
            {
                Name = Attribute(element, "name") ?? fallbackName,
                Timestamp = ParseTimestamp(Attribute(element, "timestamp")),
            };
            foreach (var testCase in testCaseElements)
            {
                var result = ParseJUnitCase(testCase, suite.Name, sourcePath);
                suite.Cases.Add(result);
            }

            suite.Duration = ParseDuration(Attribute(element, "time")) ?? SumDurations(suite.Cases);
            suites.Add(suite);
        }

        return suites;
    }

    private static TestCaseResult ParseJUnitCase(XElement element, string suiteName, string? sourcePath)
    {
        var error = Child(element, "error");
        var failure = Child(element, "failure");
        var skipped = Child(element, "skipped");
        var status = error is not null
            ? TestStatus.Errored
            : failure is not null
                ? TestStatus.Failed
                : skipped is not null
                    ? TestStatus.Skipped
                    : TestStatus.Passed;
        var problem = error ?? failure ?? skipped;

        var result = new TestCaseResult
        {
            Name = Attribute(element, "name") ?? "(unnamed test)",
            ClassName = Attribute(element, "classname"),
            SuiteName = suiteName,
            Status = status,
            Duration = ParseDuration(Attribute(element, "time")),
            Message = problem is null ? null : Attribute(problem, "message"),
            FilePath = Attribute(element, "file"),
            Line = ParseLine(Attribute(element, "line")),
        };

        if (status is TestStatus.Failed or TestStatus.Errored && problem is not null)
        {
            var trace = problem.Value.Trim();
            result.StackTrace = trace.Length > 0 ? trace : null;
        }

        AddOutputAttachment(element, result, "system-out", "System out");
        AddOutputAttachment(element, result, "system-err", "System err");
        foreach (var attachment in element.Elements().Where(child => child.Name.LocalName == "attachment"))
        {
            var path = Attribute(attachment, "path") ?? Attribute(attachment, "file");
            var content = path is null ? attachment.Value.Trim() : null;
            if (path is null && string.IsNullOrEmpty(content))
            {
                continue;
            }

            result.Attachments.Add(new TestAttachment
            {
                Name = Attribute(attachment, "name") ?? "attachment",
                Path = ResolveAttachmentPath(path, sourcePath),
                MediaType = Attribute(attachment, "type"),
                Content = content,
            });
        }

        return result;
    }

    private static void AddOutputAttachment(
        XElement element,
        TestCaseResult result,
        string localName,
        string name)
    {
        var output = Child(element, localName);
        var content = output?.Value.Trim();
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        result.Attachments.Add(new TestAttachment { Name = name, Content = content });
    }

    private static string? ResolveAttachmentPath(string? path, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(sourcePath))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
        return string.IsNullOrWhiteSpace(directory)
            ? path
            : Path.GetFullPath(Path.Combine(directory, path));
    }

    private static TimeSpan? SumDurations(IReadOnlyList<TestCaseResult> cases)
    {
        var durations = cases
            .Select(test => test.Duration)
            .Where(duration => duration is not null)
            .ToArray();
        return durations.Length == 0 ? null : durations.Aggregate(TimeSpan.Zero, (total, next) => total + next!.Value);
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
            ? timestamp
            : null;

    private static int? ParseLine(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) && line >= 0
            ? line
            : null;

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;

    private static XElement? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => child.Name.LocalName == localName);
}
