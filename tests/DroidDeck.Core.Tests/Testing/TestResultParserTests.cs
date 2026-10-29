using DroidDeck.Core.Testing;

namespace DroidDeck.Core.Tests.Testing;

public sealed class TestResultParserTests
{
    [Fact]
    public void ParseJUnit_BuildsSuiteCaseTreeWithStatusesDurationsStackTracesAndAttachments()
    {
        const string xml = """
            <testsuites>
              <testsuite name="LoginTests" tests="3" failures="1" skipped="1" time="3.5">
                <testcase name="logsIn" classname="com.example.LoginTests" time="1.25" file="LoginTests.kt" line="42" />
                <testcase name="rejectsBadPassword" classname="com.example.LoginTests" time="2" file="LoginTests.kt" line="50">
                  <failure message="expected true" type="AssertionError"><![CDATA[AssertionError: expected true
                at com.example.LoginTests.rejectsBadPassword(LoginTests.kt:52)]]></failure>
                  <system-out>debug output</system-out>
                  <attachment name="screenshot" path="artifacts/failure.png" type="image/png" />
                </testcase>
                <testcase name="isSkipped" classname="com.example.LoginTests" time="0.25">
                  <skipped message="not supported" />
                </testcase>
              </testsuite>
            </testsuites>
            """;

        var suites = TestResultParser.Parse(
            TestResultFormat.JUnitXml,
            xml,
            @"C:\results\fragment\app.xml");

        var suite = Assert.Single(suites);
        Assert.Equal("LoginTests", suite.Name);
        Assert.Equal(TimeSpan.FromSeconds(3.5), suite.Duration);
        Assert.Equal(3, suite.Cases.Count);
        Assert.Equal(1, suite.FailedCount);
        Assert.Equal(1, suite.SkippedCount);

        var passed = Assert.Single(suite.Cases, test => test.Name == "logsIn");
        Assert.Equal(TestStatus.Passed, passed.Status);
        Assert.Equal(TimeSpan.FromSeconds(1.25), passed.Duration);
        Assert.Equal("LoginTests.kt", passed.FilePath);
        Assert.Equal(42, passed.Line);

        var failed = Assert.Single(suite.Cases, test => test.Name == "rejectsBadPassword");
        Assert.Equal(TestStatus.Failed, failed.Status);
        Assert.Equal("expected true", failed.Message);
        Assert.Contains("LoginTests.kt:52", failed.StackTrace, StringComparison.Ordinal);
        Assert.Equal("com.example.LoginTests", failed.ClassName);
        Assert.Contains(
            failed.Attachments,
            attachment => attachment.Name == "System out" && attachment.Content == "debug output");
        var screenshot = Assert.Single(failed.Attachments, attachment => attachment.Name == "screenshot");
        Assert.Equal(@"C:\results\fragment\artifacts\failure.png", screenshot.Path);
        Assert.Equal("image/png", screenshot.MediaType);

        var skipped = Assert.Single(suite.Cases, test => test.Name == "isSkipped");
        Assert.Equal(TestStatus.Skipped, skipped.Status);
        Assert.Equal("not supported", skipped.Message);
    }

    [Fact]
    public void ParseJUnit_MalformedXmlReportsTheSourceFile()
    {
        var exception = Assert.Throws<TestResultParseException>(() => TestResultParser.Parse(
            TestResultFormat.JUnitXml,
            "<testsuite><testcase></testsuite>",
            @"C:\results\broken.xml"));

        Assert.Contains(@"C:\results\broken.xml", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public void ParseFlutterJson_BuildsCasesFromEventsGroupedByFile()
    {
        const string output = """
            {"protocolVersion":"0.1.1","type":"start","time":0}
            {"test":{"id":1,"name":"login rejects bad password","url":"file:///C:/proj/test/login_test.dart","line":12},"type":"testStart","time":10}
            {"testID":1,"error":"Expected: true","stackTrace":"#0 main (file:///C:/proj/test/login_test.dart:20:5)","isFailure":true,"type":"error","time":20}
            {"testID":1,"result":"failure","hidden":false,"skipped":false,"type":"testDone","time":260}
            {"test":{"id":2,"name":"login succeeds","url":"file:///C:/proj/test/login_test.dart","line":25},"type":"testStart","time":30}
            {"testID":2,"result":"success","hidden":false,"skipped":false,"type":"testDone","time":80}
            {"test":{"id":3,"name":"login is ignored","url":"file:///C:/proj/test/login_test.dart","line":30},"type":"testStart","time":90}
            {"testID":3,"result":"success","hidden":false,"skipped":true,"type":"testDone","time":95}
            {"success":false,"type":"done","time":300}
            """;

        var suites = TestResultParser.Parse(TestResultFormat.FlutterJson, output);

        var suite = Assert.Single(suites);
        Assert.Equal("login_test.dart", suite.Name);
        Assert.Equal(3, suite.Cases.Count);
        Assert.Equal(1, suite.PassedCount);
        Assert.Equal(1, suite.FailedCount);
        Assert.Equal(1, suite.SkippedCount);

        var failed = Assert.Single(suite.Cases, test => test.Name == "login rejects bad password");
        Assert.Equal(TestStatus.Failed, failed.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(250), failed.Duration);
        Assert.Equal(12, failed.Line);
        Assert.EndsWith(@"test\login_test.dart", failed.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Expected: true", failed.Message);
        Assert.Contains("login_test.dart:20", failed.StackTrace, StringComparison.Ordinal);

        var passed = Assert.Single(suite.Cases, test => test.Name == "login succeeds");
        Assert.Equal(TestStatus.Passed, passed.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(50), passed.Duration);

        var skipped = Assert.Single(suite.Cases, test => test.Name == "login is ignored");
        Assert.Equal(TestStatus.Skipped, skipped.Status);
    }

    [Fact]
    public void ParseFlutterJson_MalformedJsonReportsTheSourcePath()
    {
        var exception = Assert.Throws<TestResultParseException>(() => TestResultParser.Parse(
            TestResultFormat.FlutterJson,
            "{\"type\":\"testStart\"",
            "flutter-output.json"));

        Assert.Contains("flutter-output.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseJestJson_BuildsSuitesAndFailureDetails()
    {
        const string json = """
            {
              "testResults": [
                {
                  "name": "C:\\proj\\__tests__\\login.test.tsx",
                  "status": "failed",
                  "startTime": 1700000000000,
                  "endTime": 1700000001250,
                  "message": "",
                  "assertionResults": [
                    {
                      "ancestorTitles": ["Login"],
                      "title": "rejects bad password",
                      "fullName": "Login rejects bad password",
                      "status": "failed",
                      "duration": 12,
                      "failureMessages": ["Error: expected false to be true\n    at Object.<anonymous> (__tests__/login.test.tsx:14:5)"],
                      "location": { "line": 14, "column": 5 }
                    },
                    {
                      "ancestorTitles": ["Login"],
                      "title": "accepts",
                      "fullName": "Login accepts",
                      "status": "passed",
                      "duration": 3,
                      "failureMessages": []
                    },
                    {
                      "ancestorTitles": ["Login"],
                      "title": "is pending",
                      "fullName": "Login is pending",
                      "status": "pending",
                      "duration": null,
                      "failureMessages": []
                    }
                  ]
                }
              ]
            }
            """;

        var suites = TestResultParser.Parse(TestResultFormat.JestJson, json);

        var suite = Assert.Single(suites);
        Assert.Equal("login.test.tsx", suite.Name);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), suite.Duration);
        Assert.Equal(3, suite.Cases.Count);
        Assert.Equal(1, suite.FailedCount);

        var failed = Assert.Single(suite.Cases, test => test.Name == "rejects bad password");
        Assert.Equal(TestStatus.Failed, failed.Status);
        Assert.Equal("Login", failed.ClassName);
        Assert.Equal(TimeSpan.FromMilliseconds(12), failed.Duration);
        Assert.Equal(14, failed.Line);
        Assert.Equal(@"C:\proj\__tests__\login.test.tsx", failed.FilePath);
        Assert.Equal("Error: expected false to be true", failed.Message);
        Assert.Contains("login.test.tsx:14", failed.StackTrace, StringComparison.Ordinal);

        var skipped = Assert.Single(suite.Cases, test => test.Name == "is pending");
        Assert.Equal(TestStatus.Skipped, skipped.Status);
        Assert.Null(skipped.Duration);
    }

    [Fact]
    public void ParseJestJson_MalformedJsonReportsTheSourcePath()
    {
        var exception = Assert.Throws<TestResultParseException>(() => TestResultParser.Parse(
            TestResultFormat.JestJson,
            "{\"testResults\": [",
            "jest-results.json"));

        Assert.Contains("jest-results.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTrxXml_BuildsSuiteFromClassAndReadsOutput()
    {
        const string xml = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times start="2026-10-29T10:00:00.0000000Z" finish="2026-10-29T10:00:02.5000000Z" />
              <Results>
                <UnitTestResult testId="t1" testName="Sample.LoginTests.rejectsBadPassword" duration="00:00:01.2500000" outcome="Failed">
                  <Output>
                    <ErrorInfo>
                      <Message>Assert.Equal() Failure</Message>
                      <StackTrace>   at Sample.LoginTests.rejectsBadPassword() in LoginTests.cs:line 42</StackTrace>
                    </ErrorInfo>
                    <StdOut>debug output</StdOut>
                    <ResultFiles>
                      <ResultFile path="C:\proj\TestResults\failure.png" />
                    </ResultFiles>
                  </Output>
                </UnitTestResult>
                <UnitTestResult testId="t2" testName="Sample.LoginTests.logsIn" duration="00:00:00.5000000" outcome="Passed" />
                <UnitTestResult testId="t3" testName="Sample.LoginTests.isIgnored" duration="00:00:00" outcome="NotExecuted" />
              </Results>
              <TestDefinitions>
                <UnitTest name="rejectsBadPassword" storage="Sample.dll" id="t1">
                  <TestMethod codeBase="Sample.dll" className="Sample.LoginTests" name="rejectsBadPassword" />
                </UnitTest>
                <UnitTest name="logsIn" storage="Sample.dll" id="t2">
                  <TestMethod codeBase="Sample.dll" className="Sample.LoginTests" name="logsIn" />
                </UnitTest>
                <UnitTest name="isIgnored" storage="Sample.dll" id="t3">
                  <TestMethod codeBase="Sample.dll" className="Sample.LoginTests" name="isIgnored" />
                </UnitTest>
              </TestDefinitions>
            </TestRun>
            """;

        var suites = TestResultParser.Parse(TestResultFormat.TrxXml, xml);

        var suite = Assert.Single(suites);
        Assert.Equal("Sample.LoginTests", suite.Name);
        Assert.Equal(TimeSpan.FromSeconds(2.5), suite.Duration);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T10:00:00Z"), suite.Timestamp);
        Assert.Equal(3, suite.Cases.Count);
        Assert.Equal(1, suite.FailedCount);
        Assert.Equal(1, suite.SkippedCount);

        var failed = Assert.Single(suite.Cases, test => test.Name == "rejectsBadPassword");
        Assert.Equal(TestStatus.Failed, failed.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), failed.Duration);
        Assert.Equal("Assert.Equal() Failure", failed.Message);
        Assert.Contains("LoginTests.cs:line 42", failed.StackTrace, StringComparison.Ordinal);
        Assert.Contains(
            failed.Attachments,
            attachment => attachment.Name == "Standard output" && attachment.Content == "debug output");
        Assert.Contains(
            failed.Attachments,
            attachment => attachment.Path == @"C:\proj\TestResults\failure.png");

        var skipped = Assert.Single(suite.Cases, test => test.Name == "isIgnored");
        Assert.Equal(TestStatus.Skipped, skipped.Status);
    }

    [Fact]
    public void ParseTrxXml_RejectsNonTrxDocuments()
    {
        var exception = Assert.Throws<TestResultParseException>(() => TestResultParser.Parse(
            TestResultFormat.TrxXml,
            "<html></html>",
            @"C:\results\results.trx"));

        Assert.Contains(@"C:\results\results.trx", exception.Message, StringComparison.Ordinal);
    }
}
