using DroidDeck.Core.Runs;

namespace DroidDeck.Core.Tests.Runs;

public sealed class BuildErrorParserTests
{
    [Fact]
    public void ParseLine_ParsesKotlinCompilerError()
    {
        var error = BuildErrorParser.ParseLine(
            "e: file:///C:/repo/app/src/main/java/Main.kt: (12, 5): Unresolved reference: missing");

        Assert.NotNull(error);
        Assert.Equal(@"C:\repo\app\src\main\java\Main.kt", error.FilePath);
        Assert.Equal(12, error.Line);
        Assert.Equal(5, error.Column);
        Assert.Equal("Unresolved reference: missing", error.Message);
        Assert.Equal("error", error.Severity);
    }

    [Fact]
    public void ParseLine_ParsesJavaErrorWithRelativePath()
    {
        var error = BuildErrorParser.ParseLine(
            "app/src/main/java/com/example/Main.java:42: error: cannot find symbol",
            @"C:\repo");

        Assert.NotNull(error);
        Assert.Equal(Path.Combine(@"C:\repo", "app", "src", "main", "java", "com", "example", "Main.java"), error.FilePath);
        Assert.Equal(42, error.Line);
        Assert.Equal(0, error.Column);
        Assert.Equal("cannot find symbol", error.Message);
    }

    [Fact]
    public void ParseLine_ParsesGenericGradleError()
    {
        var error = BuildErrorParser.ParseLine("app/src/main/res/layout/main.xml:7: Error: attribute missing");

        Assert.NotNull(error);
        Assert.Equal(Path.Combine("app", "src", "main", "res", "layout", "main.xml"), error.FilePath);
        Assert.Equal(7, error.Line);
        Assert.Equal("attribute missing", error.Message);
    }

    [Fact]
    public void Parse_DeduplicatesRepeatedErrors()
    {
        const string output = """
            e: C:\repo\Main.kt: (10, 2): unresolved reference: foo
            e: C:\repo\Main.kt: (10, 2): unresolved reference: foo
            BUILD FAILED
            """;

        var errors = BuildErrorParser.Parse(output);

        Assert.Single(errors);
    }

    [Theory]
    [InlineData("BUILD SUCCESSFUL in 3s")]
    [InlineData("warning: something might be wrong")]
    [InlineData("> Task :app:assembleDebug")]
    public void ParseLine_IgnoresNonErrors(string line)
    {
        Assert.Null(BuildErrorParser.ParseLine(line));
    }

    [Fact]
    public void ParseLine_ParsesDartCompilerError()
    {
        var error = BuildErrorParser.ParseLine(
            "lib/main.dart:10:5: Error: Expected ';' after this.",
            @"C:\repo");

        Assert.NotNull(error);
        Assert.Equal(Path.Combine(@"C:\repo", "lib", "main.dart"), error.FilePath);
        Assert.Equal(10, error.Line);
        Assert.Equal(5, error.Column);
        Assert.Equal("Expected ';' after this.", error.Message);
    }

    [Fact]
    public void Parse_ParsesFlutterBuildFailureOutput()
    {
        const string output = """
            Building Flutter application...
            lib/main.dart:10:5: Error: Expected ';' after this.
            Target kernel_snapshot_program failed: Exception
            BUILD FAILED in 4s
            """;

        var error = Assert.Single(BuildErrorParser.Parse(output, @"C:\repo"));

        Assert.EndsWith("main.dart", error.FilePath, StringComparison.Ordinal);
        Assert.Equal(10, error.Line);
    }

    [Fact]
    public void Parse_ReturnsEmptyForBlankOutput()
    {
        Assert.Empty(BuildErrorParser.Parse(null));
        Assert.Empty(BuildErrorParser.Parse("   "));
    }

    [Fact]
    public void ParseLine_ParsesMsBuildErrorsWithSourceLocations()
    {
        var error = BuildErrorParser.ParseLine(
            @"C:\repo\MainPage.xaml.cs(12,5): error CS0103: The name 'missing' does not exist in the current context",
            @"C:\repo");

        Assert.NotNull(error);
        Assert.Equal(Path.Combine(@"C:\repo", "MainPage.xaml.cs"), error.FilePath);
        Assert.Equal(12, error.Line);
        Assert.Equal(5, error.Column);
        Assert.Equal("CS0103", error.Code);
        Assert.Equal("The name 'missing' does not exist in the current context", error.Message);
    }

    [Fact]
    public void ParseLine_IgnoresMsBuildWarnings()
    {
        Assert.Null(BuildErrorParser.ParseLine(
            @"C:\repo\MainPage.xaml.cs(12,5): warning CS0618: 'Thing' is obsolete"));
    }

    [Fact]
    public void ParseLine_ParsesMissingMauiWorkloadWithTheInstallCommand()
    {
        var error = BuildErrorParser.ParseLine(
            "error NETSDK1147: To build this project, the following workloads must be installed: maui-android");

        Assert.NotNull(error);
        Assert.Equal("NETSDK1147", error.Code);
        Assert.Contains("dotnet workload install maui-android", error.Guidance, StringComparison.Ordinal);
        Assert.Contains("dotnet workload install maui-android", error.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLine_ParsesMissingAndroidSdkAndJdkGuidance()
    {
        var sdk = BuildErrorParser.ParseLine(
            "error XA5207: Could not find android.jar for API level 34. This means the Android SDK platform for API level 34 is not installed.");
        var jdk = BuildErrorParser.ParseLine("error XA0030: Building with JDK version 11 is not supported.");

        Assert.NotNull(sdk);
        Assert.Contains("Android SDK", sdk.Guidance, StringComparison.Ordinal);
        Assert.NotNull(jdk);
        Assert.Contains("JDK", jdk.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void Display_UsesFileNameLineAndColumn()
    {
        var error = new BuildError
        {
            FilePath = @"C:\repo\Main.kt",
            Line = 12,
            Column = 5,
            Message = "boom",
        };

        Assert.Equal("Main.kt:12:5 boom", error.Display);
    }
}
