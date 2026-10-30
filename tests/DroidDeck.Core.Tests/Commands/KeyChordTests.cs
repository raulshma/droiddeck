using DroidDeck.Core.Commands;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Commands;

public sealed class KeyChordTests
{
    [Fact]
    public void ChordParsesAndNormalizesMultipleGestures()
    {
        var chord = KeyChord.Parse("ctrl+k ctrl+shift+c");

        Assert.Equal(2, chord.Steps.Count);
        Assert.Equal(KeyGesture.Parse("Ctrl+K"), chord.Steps[0]);
        Assert.Equal("Ctrl+K Ctrl+Shift+C", chord.ToString());
    }

    [Fact]
    public void ChordRejectsMoreThanThreeStepsAndModifierOnlySteps()
    {
        Assert.False(KeyChord.TryParse("Ctrl+K Ctrl+C Ctrl+X Ctrl+V", out _));
        Assert.False(KeyChord.TryParse("Ctrl+Shift", out _));
        Assert.True(KeyChord.TryParse("F5", out var single));
        Assert.True(single.IsSingleStep);
    }

    [Fact]
    public void ChordEqualityIsOrderSensitive()
    {
        Assert.Equal(KeyChord.Parse("Ctrl+K Ctrl+C"), KeyChord.Parse("Ctrl+K Ctrl+C"));
        Assert.NotEqual(KeyChord.Parse("Ctrl+K Ctrl+C"), KeyChord.Parse("Ctrl+C Ctrl+K"));
    }
}
