using System.Linq;
using VsAgentic.Services.Dictation;
using Xunit;

namespace VsAgentic.Services.Tests;

public class DictationShortcutTests
{
    [Fact]
    public void A_modifier_chord_with_a_key()
    {
        var s = DictationShortcut.Parse("Ctrl+Shift+M")!;
        Assert.True(s.Ctrl);
        Assert.True(s.Shift);
        Assert.False(s.Alt);
        Assert.Equal("M", s.Key);
        Assert.Empty(s.Sides);
        Assert.Equal("Ctrl + Shift + M", s.Display);
    }

    [Theory]
    [InlineData("RightCtrl+RightShift")]
    [InlineData("right ctrl + right shift")]
    [InlineData("RCtrl+RShift")]
    public void Right_hand_modifiers_alone_are_a_shortcut(string text)
    {
        var s = DictationShortcut.Parse(text)!;
        Assert.Null(s.Key);
        Assert.True(s.Ctrl);
        Assert.True(s.Shift);
        Assert.Equal(new[] { "RightCtrl", "RightShift" }, s.Sides.ToArray());
        Assert.Equal("Right Ctrl + Right Shift", s.Display);
    }

    [Theory]
    [InlineData("ctrl+shift+m", "M")]
    [InlineData("Alt+F9", "F9")]
    [InlineData("Ctrl+1", "D1")]
    [InlineData("Ctrl+Space", "Space")]
    public void The_closing_key_is_normalised(string text, string key)
    {
        Assert.Equal(key, DictationShortcut.Parse(text)!.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("M")]                 // a key alone would fire while typing
    [InlineData("Ctrl")]              // one modifier alone would fire on every Ctrl press
    [InlineData("RightCtrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+M+N")]          // two closing keys
    [InlineData("M+Ctrl")]            // the key must come last
    [InlineData("Ctrl+12")]           // a number, not a key
    [InlineData("Ctrl+%")]
    public void Anything_else_is_not_a_shortcut(string? text)
    {
        Assert.Null(DictationShortcut.Parse(text));
    }
}
