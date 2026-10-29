using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DroidDeck.Core.Testing;

public static partial class TestResultParser
{
    private static List<TestSuiteResult> ParseTrxXml(string content, string? sourcePath)
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
                $"The TRX XML could not be parsed: {exception.Message}",
                sourcePath,
                exception);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "TestRun")
        {
            throw new TestResultParseException(
                "The XML does not contain a TRX <TestRun> element.",
                sourcePath);
        }

        var classNames = ReadTrxClassNames(root);
        var suitesByClass = new Dictionary<string, TestSuiteResult>(StringComparer.Ordinal);
        var suites = new List<TestSuiteResult>();
        var results = root
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Results");
        if (results is not null)
        {
            foreach (var element in results.Elements().Where(child => child.Name.LocalName == "UnitTestResult"))
            {
                var testId = Attribute(element, "testId");
                var testName = Attribute(element, "testName") ?? "(unnamed test)";
                var className = testId is not null && classNames.TryGetValue(testId, out var known)
                    ? known
                    : null;
                var suiteName = className ?? "Tests";
                if (!suitesByClass.TryGetValue(suiteName, out var suite))
                {
                    suite = new TestSuiteResult { Name = suiteName };
                    suitesByClass[suiteName] = suite;
                    suites.Add(suite);
                }

                suite.Cases.Add(ParseTrxCase(element, testName, className, suiteName, sourcePath));
            }
        }

        var times = root
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "Times");
        if (times is not null)
        {
            var start = ParseTimestamp(Attribute(times, "start"));
            var finish = ParseTimestamp(Attribute(times, "finish"));
            foreach (var suite in suites)
            {
                suite.Timestamp = start;
                if (start is not null && finish is not null && finish >= start)
                {
                    suite.Duration = finish - start;
                }
            }
        }

        return suites;
    }

    private static Dictionary<string, string> ReadTrxClassNames(XElement root)
    {
        var classNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = root
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "TestDefinitions");
        if (definitions is null)
        {
            return classNames;
        }

        foreach (var unitTest in definitions.Elements().Where(child => child.Name.LocalName == "UnitTest"))
        {
            var id = Attribute(unitTest, "id");
            var method = unitTest
                .Descendants()
                .FirstOrDefault(child => child.Name.LocalName == "TestMethod");
            var className = method is null ? null : Attribute(method, "className");
            if (id is not null && !string.IsNullOrWhiteSpace(className))
            {
                classNames[id] = className;
            }
        }

        return classNames;
    }

    private static TestCaseResult ParseTrxCase(
        XElement element,
        string testName,
        string? className,
        string suiteName,
        string? sourcePath)
    {
        var name = className is not null && testName.StartsWith(className + ".", StringComparison.Ordinal)
            ? testName[(className.Length + 1)..]
            : testName;
        var result = new TestCaseResult
        {
            Name = name,
            ClassName = className,
            SuiteName = suiteName,
            Status = ParseTrxOutcome(Attribute(element, "outcome")),
            Duration = ParseTrxDuration(Attribute(element, "duration")),
        };

        var output = Child(element, "Output");
        if (output is null)
        {
            return result;
        }

        var error = Child(output, "ErrorInfo");
        if (error is not null)
        {
            var message = Child(error, "Message")?.Value.Trim();
            var trace = Child(error, "StackTrace")?.Value.Trim();
            result.Message = string.IsNullOrEmpty(message) ? null : message;
            result.StackTrace = string.IsNullOrEmpty(trace) ? null : trace;
        }

        var standardOutput = Child(output, "StdOut")?.Value.Trim();
        if (!string.IsNullOrEmpty(standardOutput))
        {
            result.Attachments.Add(new TestAttachment
            {
                Name = "Standard output",
                Content = standardOutput,
            });
        }

        var files = Child(output, "ResultFiles");
        if (files is not null)
        {
            foreach (var file in files.Elements().Where(child => child.Name.LocalName == "ResultFile"))
            {
                var path = Attribute(file, "path");
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var fileName = Path.GetFileName(path.Replace('\\', '/'));
                result.Attachments.Add(new TestAttachment
                {
                    Name = string.IsNullOrWhiteSpace(fileName) ? "Result file" : fileName,
                    Path = ResolveAttachmentPath(path, sourcePath),
                });
            }
        }

        return result;
    }

    private static TestStatus ParseTrxOutcome(string? outcome) => outcome switch
    {
        "Passed" => TestStatus.Passed,
        "Failed" => TestStatus.Failed,
        "NotExecuted" or "Skipped" or "Inconclusive" or "Pending" => TestStatus.Skipped,
        _ => TestStatus.Errored,
    };

    private static TimeSpan? ParseTrxDuration(string? value) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var duration) && duration >= TimeSpan.Zero
            ? duration
            : null;
}
