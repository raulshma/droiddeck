using Xunit;

namespace DroidDeck.ReleaseCheck.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void ParsesValuesAndDefaults()
    {
        var options = Options.Parse(["--runs", "5", "--json", "out.json"]);

        Assert.Equal(5, options.OptionalInt("runs", 10));
        Assert.Equal("out.json", options.Optional("json"));
        Assert.Equal(10, options.OptionalInt("missing", 10));
        Assert.Equal(TimeSpan.FromHours(8), options.OptionalDuration("missing", TimeSpan.FromHours(8)));
    }

    [Fact]
    public void RequiresValuesForEveryOption()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["--runs"]));
    }

    [Fact]
    public void RejectsPositionalArguments()
    {
        Assert.Throws<ArgumentException>(() => Options.Parse(["size"]));
    }

    [Fact]
    public void RequiredOptionThrowsWhenMissing()
    {
        var options = Options.Parse([]);

        Assert.Throws<ArgumentException>(() => options.Required("exe"));
    }
}
