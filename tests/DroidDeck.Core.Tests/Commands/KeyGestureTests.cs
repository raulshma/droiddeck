using DroidDeck.Core.Commands;

namespace DroidDeck.Core.Tests.Commands;

public sealed class KeyGestureTests
{
    [Fact]
    public void ParsesSingleKeyWithNoModifiers()
    {
        var gesture = KeyGesture.Parse("F5");

        Assert.Equal(KeyModifiers.None, gesture.Modifiers);
        Assert.Equal("F5", gesture.Key);
        Assert.Equal("F5", gesture.ToString());
    }

    [Fact]
    public void ParsesModifiersInAnyOrderAndNormalizesTheDisplay()
    {
        var gesture = KeyGesture.Parse("shift+CTRL+k");

        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, gesture.Modifiers);
        Assert.Equal("K", gesture.Key);
        Assert.Equal("Ctrl+Shift+K", gesture.ToString());
    }

    [Theory]
    [InlineData("Ctrl+Alt+Delete", KeyModifiers.Control | KeyModifiers.Alt, "Delete")]
    [InlineData("escape", KeyModifiers.None, "Escape")]
    [InlineData("space", KeyModifiers.None, "Space")]
    [InlineData("pageup", KeyModifiers.None, "PageUp")]
    [InlineData("return", KeyModifiers.None, "Enter")]
    public void ParsesAliasesIntoCanonicalForms(string text, KeyModifiers modifiers, string key)
    {
        var gesture = KeyGesture.Parse(text);

        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(key, gesture.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl++")]
    public void RejectsGesturesWithoutAKey(string? text)
    {
        Assert.False(KeyGesture.TryParse(text, out _));
        Assert.ThrowsAny<ArgumentException>(() => KeyGesture.Parse(text!));
    }

    [Fact]
    public void EqualGesturesCompareEqualRegardlessOfInputCasing()
    {
        Assert.Equal(KeyGesture.Parse("ctrl+k"), KeyGesture.Parse("CTRL+K"));
        Assert.Equal(KeyGesture.Parse("ctrl+k").GetHashCode(), KeyGesture.Parse("CTRL+k").GetHashCode());
    }
}
