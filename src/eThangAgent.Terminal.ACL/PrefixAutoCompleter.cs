namespace eThangAgent.Terminal.ACL;

/// <summary>Suggests a candidate when exactly one candidate prefix-matches the input and is not already complete.</summary>
public sealed class PrefixAutoCompleter(IReadOnlyList<string> candidates) : IAutoCompleter
{
    public string? Suggest(string input)
    {
        if (input.Length == 0)
            return null;

        string? match = null;
        var matches = 0;
        foreach (var candidate in candidates)
        {
            if (!candidate.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                continue;
            if (candidate.Equals(input, StringComparison.OrdinalIgnoreCase))
                continue;

            match = candidate;
            matches++;
        }

        return matches == 1 ? match : null;
    }
}
