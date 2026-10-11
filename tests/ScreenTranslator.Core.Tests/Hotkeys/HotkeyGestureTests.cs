using ScreenTranslator.Core.Hotkeys;

namespace ScreenTranslator.Core.Tests.Hotkeys;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+G", "Ctrl + Alt + G")]
    [InlineData("Shift+F8", "Shift + F8")]
    [InlineData("Win+Shift+S", "Win + Shift + S")]
    [InlineData("ctrl+pageup", "Ctrl + PageUp")]
    public void Parse_Normalizes_Valid_Gestures(string persisted, string display)
    {
        var gesture = HotkeyGesture.Parse(persisted);

        Assert.Equal(
            display.Replace(" + ", "+", StringComparison.Ordinal),
            gesture.ToPersistedString());
        Assert.Equal(display, gesture.ToDisplayString());
    }

    [Theory]
    [InlineData("T")]
    [InlineData("Ctrl")]
    [InlineData("Alt+Shift")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Ctrl++G")]
    [InlineData("Ctrl+Ctrl+G")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Tab")]
    public void Parse_Rejects_Unsafe_Or_Incomplete_Gestures(string persisted) =>
        Assert.Throws<FormatException>(() => HotkeyGesture.Parse(persisted));

    [Fact]
    public void Default_Is_Stable_And_Displayable()
    {
        Assert.Equal("Alt+Shift+T", HotkeyGesture.Default.ToPersistedString());
        Assert.Equal("Alt + Shift + T", HotkeyGesture.Default.ToDisplayString());
    }
}
