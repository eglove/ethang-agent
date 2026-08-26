namespace eThangAgent.MemoryDomain;

/// <summary>Empty or whitespace query — recent entries, newest first.</summary>
public sealed record BrowsePlan : MemoryQueryPlan;

/// <summary>Distinct canonical lexical tokens of the literal query, in first-occurrence order.</summary>
public sealed record TermsPlan(IReadOnlyList<string> Tokens) : MemoryQueryPlan;

/// <summary>Raw regex pattern, passed through unvalidated — BoundedRegex owns validation.</summary>
public sealed record RegexPatternPlan(string Pattern) : MemoryQueryPlan;

/// <summary>
/// Plans only the explicitly selected query mode. Literal input is never compiled as a regex;
/// planning happens once, in the domain.
/// </summary>
public abstract record MemoryQueryPlan
{
  public static MemoryQueryPlan Plan(string? query, string queryMode = "literal")
  {
    if (string.IsNullOrWhiteSpace(query))
    {
      return new BrowsePlan();
    }

    switch (queryMode)
    {
      case "regex":
        return new RegexPatternPlan(query);
      case "literal":
        IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize(query);
        List<string> distinct = new(tokens.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
          if (seen.Add(token))
          {
            distinct.Add(token);
          }
        }

        return new TermsPlan(distinct);
      default:
        // Programmer error: the capability layer validates the mode string before calling.
        throw new ArgumentException(
            $"Unknown queryMode '{queryMode}'. Valid modes: literal | regex.", nameof(queryMode));
    }
  }
}
