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
    // Canonical token form is LOWERCASE: persisted memories were written lowercased.
    // CA1308 suggests ToUpperInvariant, which would break every stored token.
#pragma warning disable CA1308 // Normalize strings to uppercase

    return [.. TokenPattern.Matches(normalized).Select(m => m.Value.ToLowerInvariant())];
#pragma warning restore CA1308 // Normalize strings to uppercase
  }
}
