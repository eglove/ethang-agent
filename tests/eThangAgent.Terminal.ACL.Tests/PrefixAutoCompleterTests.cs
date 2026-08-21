using eThangAgent.Terminal.ACL;

namespace eThangAgent.Terminal.ACL.Tests;

public class PrefixAutoCompleterTests
{
    private readonly PrefixAutoCompleter _completer = new(["/exit", "/help", "/quit"]);

    [Theory]
    [InlineData("/e", "/exit")]
    [InlineData("/h", "/help")]
    [InlineData("/q", "/quit")]
    public void UniquePrefix_ReturnsCompletion(string input, string expected)
    {
        Assert.Equal(expected, _completer.Suggest(input));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("hello")]
    [InlineData("/x")]
    [InlineData("/exit")]
    public void AmbiguousOrNonMatchingOrComplete_ReturnsNull(string input)
    {
        Assert.Null(_completer.Suggest(input));
    }
}
