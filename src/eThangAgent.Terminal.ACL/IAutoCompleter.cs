namespace eThangAgent.Terminal.ACL;

/// <summary>Proposes a full replacement for the current input line, or <c>null</c> for no suggestion.</summary>
public interface IAutoCompleter
{
    string? Suggest(string input);
}
