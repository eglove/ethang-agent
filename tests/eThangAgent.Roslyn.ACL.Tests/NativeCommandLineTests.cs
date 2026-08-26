namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Pins NativeCommandLine.Split to Windows argv (CommandLineToArgvW)
/// semantics: whitespace splits outside quotes, quoted segments stay one token,
/// doubled quotes inside quotes are a literal quote, and backslash runs before a
/// quote follow the 2n/n and 2n+1/literal-quote rules.</summary>
public class NativeCommandLineTests
{
  [Fact]
  public void MultiTokenString_SplitsOnWhitespace()
  {
    IReadOnlyList<string> tokens = NativeCommandLine.Split("build -c Release");
    Assert.Equal(["build", "-c", "Release"], tokens);
  }

  [Fact]
  public void QuotedSegment_StaysSingleToken()
  {
    IReadOnlyList<string> tokens = NativeCommandLine.Split("git init \"C:/tmp/a b\"");
    Assert.Equal(["git", "init", "C:/tmp/a b"], tokens);
  }

  [Fact]
  public void DoubledQuotesInsideQuotes_AreOneLiteralQuote()
  {
    IReadOnlyList<string> tokens = NativeCommandLine.Split("\"a\"\"b\"");
    Assert.Equal(["a\"b"], tokens);
  }

  [Fact]
  public void OddBackslashRunBeforeQuote_EmitsLiteralQuote()
  {
    IReadOnlyList<string> tokens = NativeCommandLine.Split("echo \\\"hi\\\"");
    Assert.Equal(["echo", "\"hi\""], tokens);
  }

  [Fact]
  public void EvenBackslashRunBeforeQuote_HalvesBackslashesAndToggles()
  {
    IReadOnlyList<string> tokens = NativeCommandLine.Split("dir \"C:\\src\\\\\" extra");
    Assert.Equal(["dir", "C:\\src\\", "extra"], tokens);
  }

  [Fact]
  public void EmptyQuotedArgument_IsPreserved()
  {
    IReadOnlyList<string> tokens = NativeCommandLine.Split("cmd \"\" x");
    Assert.Equal(["cmd", "", "x"], tokens);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("\t\t")]
  public void BlankInput_YieldsNoTokens(string input) => Assert.Empty(NativeCommandLine.Split(input));
}
