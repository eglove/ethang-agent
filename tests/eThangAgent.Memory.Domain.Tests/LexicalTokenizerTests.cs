namespace eThangAgent.MemoryDomain.Tests;

public class LexicalTokenizerTests
{
  [Fact]
  public void Tokenize_Null_ReturnsEmptyList()
  {
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize(null!); // deliberate contract probe: null must yield empty
    Assert.Empty(tokens);
  }

  [Fact]
  public void Tokenize_EmptyString_ReturnsEmptyList() => Assert.Empty(LexicalTokenizer.Tokenize(string.Empty));

  [Fact]
  public void Tokenize_AsciiWords_SplitsAndLowercases()
  {
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize("Hello World");
    Assert.Equal(["hello", "world"], tokens);
  }

  [Fact]
  public void Tokenize_CyrillicLetters_TokenizesLowercased()
  {
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize("Привет Мир");
    Assert.Equal(["привет", "мир"], tokens);
  }

  [Fact]
  public void Tokenize_DigitsAndUnderscore_AreKeptWithinTokens()
  {
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize("abc_123 456 x9");
    Assert.Equal(["abc_123", "456", "x9"], tokens);
  }

  [Fact]
  public void Tokenize_Punctuation_SplitsIntoSeparateTokens()
  {
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize("one, two; three.four (five)");
    Assert.Equal(["one", "two", "three", "four", "five"], tokens);
  }

  [Fact]
  public void Tokenize_NfkcLigature_FiLigature_BecomesFi()
  {
    // U+FB01 LATIN SMALL LIGATURE FI normalizes under NFKC to "fi".
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize("\uFB01le");
    Assert.Equal(["file"], tokens);
  }

  [Fact]
  public void Tokenize_MixedCase_FoldsToLowercase()
  {
    IReadOnlyList<string> tokens = LexicalTokenizer.Tokenize("ABC DeF ghi");
    Assert.Equal(["abc", "def", "ghi"], tokens);
  }
}
