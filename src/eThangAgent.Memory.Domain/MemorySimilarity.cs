namespace eThangAgent.MemoryDomain;

/// <summary>
/// Token-set similarity for the search-before-add guard: Jaccard over canonical
/// lexical tokens (<see cref="LexicalTokenizer"/>), so case and punctuation never
/// inflate or deflate the score. Empty token sets score 0 — nothing in common.
/// </summary>
public static class MemorySimilarity
{
  public static double Jaccard(string left, string right)
  {
    IReadOnlyList<string> leftTokens = LexicalTokenizer.Tokenize(left);
    IReadOnlyList<string> rightTokens = LexicalTokenizer.Tokenize(right);
    if (leftTokens.Count == 0 || rightTokens.Count == 0)
    {
      return 0.0;
    }

    HashSet<string> union = [.. leftTokens, .. rightTokens];
    int intersection = leftTokens.Count + rightTokens.Count - union.Count;
    return (double)intersection / union.Count;
  }
}
