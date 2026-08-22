namespace eThangAgent.MemoryDomain.Tests;

public class LexicalTokenizerTests
{
    [Fact]
    public void Tokenize_Null_ReturnsEmptyList()
    {
        var tokens = LexicalTokenizer.Tokenize(null!); // deliberate contract probe: null must yield empty
        Assert.Empty(tokens);
    }

    [Fact]
    public void Tokenize_EmptyString_ReturnsEmptyList()
    {
        Assert.Empty(LexicalTokenizer.Tokenize(string.Empty));
    }

    [Fact]
    public void Tokenize_AsciiWords_SplitsAndLowercases()
    {
        var tokens = LexicalTokenizer.Tokenize("Hello World");
        Assert.Equal(new[] { "hello", "world" }, tokens);
    }

    [Fact]
    public void Tokenize_CyrillicLetters_TokenizesLowercased()
    {
        var tokens = LexicalTokenizer.Tokenize("Привет Мир");
        Assert.Equal(new[] { "привет", "мир" }, tokens);
    }

    [Fact]
    public void Tokenize_DigitsAndUnderscore_AreKeptWithinTokens()
    {
        var tokens = LexicalTokenizer.Tokenize("abc_123 456 x9");
        Assert.Equal(new[] { "abc_123", "456", "x9" }, tokens);
    }

    [Fact]
    public void Tokenize_Punctuation_SplitsIntoSeparateTokens()
    {
        var tokens = LexicalTokenizer.Tokenize("one, two; three.four (five)");
        Assert.Equal(new[] { "one", "two", "three", "four", "five" }, tokens);
    }

    [Fact]
    public void Tokenize_NfkcLigature_FiLigature_BecomesFi()
    {
        // U+FB01 LATIN SMALL LIGATURE FI normalizes under NFKC to "fi".
        var tokens = LexicalTokenizer.Tokenize("\uFB01le");
        Assert.Equal(new[] { "file" }, tokens);
    }

    [Fact]
    public void Tokenize_MixedCase_FoldsToLowercase()
    {
        var tokens = LexicalTokenizer.Tokenize("ABC DeF ghi");
        Assert.Equal(new[] { "abc", "def", "ghi" }, tokens);
    }
}
