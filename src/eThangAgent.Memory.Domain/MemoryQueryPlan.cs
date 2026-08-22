namespace eThangAgent.MemoryDomain;

/// <summary>
/// Plans only the explicitly selected query mode. Literal input is never compiled as a regex;
/// planning happens once, in the domain.
/// </summary>
public abstract record MemoryQueryPlan
{
    /// <summary>Empty or whitespace query — recent entries, newest first.</summary>
    public sealed record Browse : MemoryQueryPlan;

    /// <summary>Distinct canonical lexical tokens of the literal query, in first-occurrence order.</summary>
    public sealed record Terms(IReadOnlyList<string> Tokens) : MemoryQueryPlan;

    /// <summary>Raw regex pattern, passed through unvalidated — BoundedRegex owns validation.</summary>
    public sealed record RegexPattern(string Pattern) : MemoryQueryPlan;

    public static MemoryQueryPlan Plan(string? query, string queryMode = "literal")
    {
        if (string.IsNullOrWhiteSpace(query)) return new Browse();

        switch (queryMode)
        {
            case "regex":
                return new RegexPattern(query);
            case "literal":
                var tokens = LexicalTokenizer.Tokenize(query);
                var distinct = new List<string>(tokens.Count);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var token in tokens)
                    if (seen.Add(token))
                        distinct.Add(token);
                return new Terms(distinct);
            default:
                // Programmer error: the capability layer validates the mode string before calling.
                throw new ArgumentException(
                    $"Unknown queryMode '{queryMode}'. Valid modes: literal | regex.", nameof(queryMode));
        }
    }
}
