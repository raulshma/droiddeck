using System.Globalization;
using System.Text.Json;

namespace DroidDeck.Core.Testing;

public static partial class TestResultParser
{
    private static List<TestSuiteResult> ParseFlutterJson(string content, string? sourcePath)
    {
        var tests = new Dictionary<long, FlutterTestInfo>();
        var suites = new List<TestSuiteResult>();
        var suitesByFile = new Dictionary<string, TestSuiteResult>(StringComparer.OrdinalIgnoreCase);
        var sawEvent = false;
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new TestResultParseException(
                    "The Flutter test output could not be parsed as JSON.",
                    sourcePath,
                    exception);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                switch (JsonString(root, "type"))
                {
                    case "testStart":
                        sawEvent |= StartFlutterTest(root, tests);
                        break;
                    case "error":
                        CaptureFlutterError(root, tests);
                        break;
                    case "testDone":
                        sawEvent |= CompleteFlutterTest(root, tests, suites, suitesByFile);
                        break;
                }
            }
        }

        if (!sawEvent)
        {
            throw new TestResultParseException(
                "The Flutter test output did not contain any test events.",
                sourcePath);
        }

        foreach (var suite in suites)
        {
            suite.Duration = SumDurations(suite.Cases);
        }

        return suites;
    }

    private static bool StartFlutterTest(JsonElement root, Dictionary<long, FlutterTestInfo> tests)
    {
        if (!root.TryGetProperty("test", out var test) || test.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var id = JsonLong(test, "id");
        if (id is null)
        {
            return false;
        }

        tests[id.Value] = new FlutterTestInfo
        {
            Name = JsonString(test, "name") ?? "(unnamed test)",
            FilePath = FlutterTestFile(JsonString(test, "url")),
            Line = (int?)JsonLong(test, "line"),
            StartMilliseconds = JsonDouble(root, "time") ?? 0,
        };
        return true;
    }

    private static void CaptureFlutterError(JsonElement root, Dictionary<long, FlutterTestInfo> tests)
    {
        var id = JsonLong(root, "testID");
        if (id is null || !tests.TryGetValue(id.Value, out var test))
        {
            return;
        }

        test.Message = JsonString(root, "error");
        test.StackTrace = JsonString(root, "stackTrace");
    }

    private static bool CompleteFlutterTest(
        JsonElement root,
        Dictionary<long, FlutterTestInfo> tests,
        List<TestSuiteResult> suites,
        Dictionary<string, TestSuiteResult> suitesByFile)
    {
        var id = JsonLong(root, "testID");
        if (id is null || !tests.TryGetValue(id.Value, out var info) || JsonBool(root, "hidden") == true)
        {
            return false;
        }

        var status = JsonBool(root, "skipped") == true
            ? TestStatus.Skipped
            : JsonString(root, "result") switch
            {
                "success" => TestStatus.Passed,
                "failure" => TestStatus.Failed,
                "error" => TestStatus.Errored,
                _ => TestStatus.Errored,
            };
        var endMilliseconds = JsonDouble(root, "time");
        var suite = GetFlutterSuite(suites, suitesByFile, info.FilePath);
        suite.Cases.Add(new TestCaseResult
        {
            Name = info.Name,
            SuiteName = suite.Name,
            Status = status,
            Duration = endMilliseconds is null
                ? null
                : TimeSpan.FromMilliseconds(Math.Max(0, endMilliseconds.Value - info.StartMilliseconds)),
            Message = info.Message,
            StackTrace = info.StackTrace,
            FilePath = info.FilePath,
            Line = info.Line,
        });
        return true;
    }

    private static TestSuiteResult GetFlutterSuite(
        List<TestSuiteResult> suites,
        Dictionary<string, TestSuiteResult> suitesByFile,
        string? filePath)
    {
        var key = filePath ?? string.Empty;
        if (suitesByFile.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var name = string.IsNullOrWhiteSpace(filePath)
            ? "Flutter tests"
            : Path.GetFileName(filePath.Replace('\\', '/'));
        var suite = new TestSuiteResult
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Flutter tests" : name,
        };
        suitesByFile[key] = suite;
        suites.Add(suite);
        return suite;
    }

    private static string? FlutterTestFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : url;
    }

    private static List<TestSuiteResult> ParseJestJson(string content, string? sourcePath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException exception)
        {
            throw new TestResultParseException(
                "The Jest JSON could not be parsed.",
                sourcePath,
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("testResults", out var testResults) ||
                testResults.ValueKind != JsonValueKind.Array)
            {
                throw new TestResultParseException(
                    "The JSON does not contain a Jest \"testResults\" array.",
                    sourcePath);
            }

            var suites = new List<TestSuiteResult>();
            foreach (var file in testResults.EnumerateArray())
            {
                var fileName = JsonString(file, "name");
                var suite = new TestSuiteResult
                {
                    Name = string.IsNullOrWhiteSpace(fileName)
                        ? "Jest"
                        : Path.GetFileName(fileName.Replace('\\', '/')),
                    Timestamp = ParseUnixMilliseconds(JsonLong(file, "startTime")),
                    Duration = ParseMillisecondRange(JsonLong(file, "startTime"), JsonLong(file, "endTime")),
                };
                if (string.IsNullOrWhiteSpace(suite.Name))
                {
                    suite.Name = "Jest";
                }

                var hasCases = false;
                if (file.TryGetProperty("assertionResults", out var assertions) &&
                    assertions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var assertion in assertions.EnumerateArray())
                    {
                        hasCases = true;
                        suite.Cases.Add(ParseJestCase(assertion, suite.Name, fileName));
                    }
                }

                if (!hasCases)
                {
                    var message = JsonString(file, "message")?.Trim();
                    if (!string.IsNullOrEmpty(message))
                    {
                        suite.Cases.Add(new TestCaseResult
                        {
                            Name = "Test suite failed to run",
                            SuiteName = suite.Name,
                            Status = TestStatus.Errored,
                            Message = FirstLine(message),
                            StackTrace = message,
                            FilePath = fileName,
                        });
                    }
                }

                suites.Add(suite);
            }

            return suites;
        }
    }

    private static TestCaseResult ParseJestCase(JsonElement element, string suiteName, string? fileName)
    {
        var status = JsonString(element, "status") switch
        {
            "passed" => TestStatus.Passed,
            "failed" => TestStatus.Failed,
            "pending" or "skipped" or "todo" or "disabled" => TestStatus.Skipped,
            _ => TestStatus.Errored,
        };
        var className = JsonStrings(element, "ancestorTitles");
        var duration = JsonDouble(element, "duration");
        var result = new TestCaseResult
        {
            Name = JsonString(element, "title") ?? JsonString(element, "fullName") ?? "(unnamed test)",
            ClassName = className.Count == 0 ? null : string.Join('.', className),
            SuiteName = suiteName,
            Status = status,
            Duration = duration is null ? null : TimeSpan.FromMilliseconds(Math.Max(0, duration.Value)),
            FilePath = fileName,
        };
        if (element.TryGetProperty("location", out var location))
        {
            result.Line = (int?)JsonLong(location, "line");
        }

        var message = JsonStrings(element, "failureMessages").FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(message))
        {
            result.Message = FirstLine(message);
            result.StackTrace = message.Trim();
        }

        return result;
    }

    private static DateTimeOffset? ParseUnixMilliseconds(long? value) =>
        value is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    private static TimeSpan? ParseMillisecondRange(long? start, long? end) =>
        start is not null && end is not null && end >= start
            ? TimeSpan.FromMilliseconds(end.Value - start.Value)
            : null;

    private static string? FirstLine(string value)
    {
        var index = value.IndexOfAny(['\r', '\n']);
        var line = (index < 0 ? value : value[..index]).Trim();
        return line.Length == 0 ? null : line;
    }

    private static string? JsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static long? JsonLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? JsonDouble(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        return property.ValueKind == JsonValueKind.String &&
               double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool? JsonBool(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static List<string> JsonStrings(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private sealed class FlutterTestInfo
    {
        public string Name { get; set; } = string.Empty;

        public string? FilePath { get; set; }

        public int? Line { get; set; }

        public double StartMilliseconds { get; set; }

        public string? Message { get; set; }

        public string? StackTrace { get; set; }
    }
}
