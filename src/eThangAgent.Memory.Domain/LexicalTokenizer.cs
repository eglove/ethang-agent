using System.Text;
using System.Text.RegularExpressions;

namespace eThangAgent.MemoryDomain;

/// <summary>
/// Canonical Unicode-aware lexical tokens with no semantic classification.
/// Ported verbatim from pi-fabric tokenize.ts: NFKC-normalize, extract
/// <c>[\p{L}\p{N}_]+</c> matches, lowercase (invariant).
/// </summary>
public static class LexicalTokenizer
{
  private static readonly Regex TokenPattern =
      new("[\\p{L}\\p{N}_]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));

  public static IReadOnlyList<string> Tokenize(string text)
  {
    if (string.IsNullOrEmpty(text))
    {
      return [];
    }

    string normalized = text.Normalize(NormalizationForm.FormKC);
    return [.. TokenPattern.Matches(normalized).Select(m => m.Value.ToLowerInvariant())];
  }
}
