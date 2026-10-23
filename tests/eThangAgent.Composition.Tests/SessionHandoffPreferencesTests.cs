namespace eThangAgent.Composition.Tests;

/// <summary>The compaction-handoff preference key binds STRICTLY (issue #234 task 5):
///     absent means disabled; only the exact lowercase strings bind; anything else is a
///     named error, never coerced — the AGENTS.md strict-boundary convention.</summary>
public class SessionHandoffPreferencesTests
{
  [Fact]
  public void Parse_Null_BindsFalse() => Assert.False(SessionHandoffPreferences.Parse(null));

  [Fact]
  public void Parse_FalseExact_BindsFalse() => Assert.False(SessionHandoffPreferences.Parse("false"));

  [Fact]
  public void Parse_TrueExact_BindsTrue() => Assert.True(SessionHandoffPreferences.Parse("true"));

  [Theory]
  [InlineData("TRUE")]
  [InlineData("True")]
  [InlineData("FALSE")]
  [InlineData("yes")]
  [InlineData("1")]
  [InlineData(" ")]
  public void Parse_AnythingElse_ThrowsNamingTheKey(string stored)
  {
    InvalidOperationException invalid = Assert.Throws<InvalidOperationException>(
        () => SessionHandoffPreferences.Parse(stored));
    Assert.Contains(SessionHandoffPreferences.Key, invalid.Message, StringComparison.Ordinal);
  }
}
