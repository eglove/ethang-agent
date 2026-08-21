using Terminal.Gui.Drawing;
using Terminal.Gui.Views;

namespace eThangAgent.CLI;

/// <summary>Suggests slash commands for the word at the cursor; never suggests for plain text.</summary>
public sealed class CommandSuggestionGenerator(IReadOnlyList<CliCommand> commands) : ISuggestionGenerator
{
    public IEnumerable<Suggestion> GenerateSuggestions(AutocompleteContext context)
    {
        var word = CurrentWord(context);
        if (!word.StartsWith('/'))
            return [];

        return commands
            .Where(c => c.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            .Select(c => new Suggestion(word.Length, c.Name, $"{c.Name}  —  {c.Description}"));
    }

    public bool IsWordChar(string text) => IsWordCharacter(text);

    private static bool IsWordCharacter(string text) =>
        text.Length == 1 && (text[0] == '/' || char.IsLetterOrDigit(text[0]));

    private static string CurrentWord(AutocompleteContext context)
    {
        var line = context.CurrentLine;
        var end = Math.Min(context.CursorPosition, line.Count);
        var start = end;
        while (start > 0 && IsWordCharacter(line[start - 1].Grapheme ?? string.Empty))
            start--;

        return string.Concat(line[start..end].Select(c => c.Grapheme ?? string.Empty));
    }
}
