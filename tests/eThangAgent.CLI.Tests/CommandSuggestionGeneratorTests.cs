using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace eThangAgent.CLI.Tests;

public class CommandSuggestionGeneratorTests
{
    private readonly CommandSuggestionGenerator _generator = new(CliCommands.All);

    [Fact]
    public void SlashPrefix_SuggestsMatchingCommands()
    {
        var suggestions = _generator.GenerateSuggestions(Context("/e"));

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("/exit", suggestion.Replacement);
        Assert.Equal(2, suggestion.Remove);
    }

    [Fact]
    public void BareSlash_SuggestsEveryCommand()
    {
        var suggestions = _generator.GenerateSuggestions(Context("/"));

        Assert.Equal(
            CliCommands.All.Select(c => c.Name).Order(),
            suggestions.Select(s => s.Replacement).Order());
    }

    [Theory]
    [InlineData("/q")]
    [InlineData("/h")]
    public void SlashPrefix_MatchesEachCommandByPrefix(string input)
    {
        var expected = CliCommands.All.Single(c => c.Name.StartsWith(input)).Name;

        var suggestion = Assert.Single(_generator.GenerateSuggestions(Context(input)));

        Assert.Equal(expected, suggestion.Replacement);
    }

    [Fact]
    public void PlainText_ProducesNoSuggestions()
    {
        Assert.Empty(_generator.GenerateSuggestions(Context("hello")));
    }

    [Fact]
    public void UnknownSlashWord_ProducesNoSuggestions()
    {
        Assert.Empty(_generator.GenerateSuggestions(Context("/x")));
    }

    [Fact]
    public void WordBeforeCursor_IsUsedWhenCursorFollowsSpace()
    {
        var suggestion = Assert.Single(_generator.GenerateSuggestions(Context("/qu hello", cursor: 3)));

        Assert.Equal("/quit", suggestion.Replacement);
    }

    private static AutocompleteContext Context(string line, int? cursor = null)
    {
        var cells = line.Select(ch => new Cell { Grapheme = ch.ToString() }).ToList();
        return new AutocompleteContext(cells, cursor ?? line.Length, canceled: false);
    }
}
