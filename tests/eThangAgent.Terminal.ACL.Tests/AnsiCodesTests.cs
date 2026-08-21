using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class AnsiCodesTests
{
    [Fact]
    public void EnterAlternateScreen_SwitchesAndClears()
    {
        Assert.Contains("\x1b[?1049h", AnsiCodes.EnterAlternateScreen);
        Assert.Contains("\x1b[2J", AnsiCodes.EnterAlternateScreen);
    }

    [Fact]
    public void ExitAlternateScreen_LeavesAlternateBuffer()
    {
        Assert.Equal("\x1b[?1049l", AnsiCodes.ExitAlternateScreen);
    }

    [Theory]
    [InlineData(ConsoleColor.DarkGray, "\x1b[90m")]
    [InlineData(ConsoleColor.Red, "\x1b[91m")]
    [InlineData(ConsoleColor.Gray, "\x1b[37m")]
    public void ForegroundColor_MapsConsoleColorToAnsi(ConsoleColor color, string expected)
    {
        Assert.Equal(expected, AnsiCodes.ForegroundColor(color));
    }

    [Fact]
    public void ResetForeground_ReturnsDefaultForeground()
    {
        Assert.Equal("\x1b[39m", AnsiCodes.ResetForeground);
    }
}
