using DroidDeck.Core.Diagnostics;

namespace DroidDeck.Core.Tests.Diagnostics;

public sealed class FailureNoticeTests
{
    [Fact]
    public void Format_IncludesTheDiagnosticsIdWhenProvided()
    {
        var notice = FailureNotice.Format(
            "Starting the run",
            new InvalidOperationException("adb is missing"),
            "DD-ABCDEF12");

        Assert.Equal(
            "Starting the run failed: adb is missing (Diagnostics ID DD-ABCDEF12).",
            notice);
    }

    [Fact]
    public void Format_OmitsTheDiagnosticsIdWhenNotProvided()
    {
        var notice = FailureNotice.Format("Saving settings", new InvalidOperationException("disk is full"));

        Assert.Equal("Saving settings failed: disk is full.", notice);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_OmitsABlankDiagnosticsId(string? diagnosticsId)
    {
        var notice = FailureNotice.Format(
            "Saving settings",
            new InvalidOperationException("disk is full"),
            diagnosticsId);

        Assert.Equal("Saving settings failed: disk is full.", notice);
    }

    [Fact]
    public void Format_KeepsTheSentencePeriodWhenTheExceptionMessageIsEmpty()
    {
        var notice = FailureNotice.Format("Saving settings", new InvalidOperationException(string.Empty));

        Assert.Equal("Saving settings failed: .", notice);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_RejectsABlankAction(string action)
    {
        Assert.Throws<ArgumentException>(() =>
            FailureNotice.Format(action, new InvalidOperationException("boom")));
    }

    [Fact]
    public void Format_RejectsANullAction()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FailureNotice.Format(null!, new InvalidOperationException("boom")));
    }

    [Fact]
    public void Format_RejectsANullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            FailureNotice.Format("Starting the run", null!));
    }
}
