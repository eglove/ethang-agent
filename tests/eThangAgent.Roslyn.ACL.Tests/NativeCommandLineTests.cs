using eThangAgent.Roslyn.ACL;
using Xunit;

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
        var tokens = NativeCommandLine.Split("build -c Release");
        Assert.Equal(new[] { "build", "-c", "Release" }, tokens);
    }

    [Fact]
    public void QuotedSegment_StaysSingleToken()
    {
        var tokens = NativeCommandLine.Split("git init \"C:/tmp/a b\"");
        Assert.Equal(new[] { "git", "init", "C:/tmp/a b" }, tokens);
    }

    [Fact]
    public void DoubledQuotesInsideQuotes_AreOneLiteralQuote()
    {
        var tokens = NativeCommandLine.Split("\"a\"\"b\"");
        Assert.Equal(new[] { "a\"b" }, tokens);
    }

    [Fact]
    public void OddBackslashRunBeforeQuote_EmitsLiteralQuote()
    {
        var tokens = NativeCommandLine.Split("echo \\\"hi\\\"");
        Assert.Equal(new[] { "echo", "\"hi\"" }, tokens);
    }

    [Fact]
    public void EvenBackslashRunBeforeQuote_HalvesBackslashesAndToggles()
    {
        var tokens = NativeCommandLine.Split("dir \"C:\\src\\\\\" extra");
        Assert.Equal(new[] { "dir", "C:\\src\\", "extra" }, tokens);
    }

    [Fact]
    public void EmptyQuotedArgument_IsPreserved()
    {
        var tokens = NativeCommandLine.Split("cmd \"\" x");
        Assert.Equal(new[] { "cmd", "", "x" }, tokens);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\t")]
    public void BlankInput_YieldsNoTokens(string input)
    {
        Assert.Empty(NativeCommandLine.Split(input));
    }
}