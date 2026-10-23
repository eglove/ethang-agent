using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>The compaction-handoff preference key binds STRICTLY (issue #234): absent
///     means disabled; only the exact lowercase strings bind; anything else is a TYPED
///     FAILURE naming the key — never coerced, never thrown across a seam (the compactor
///     consumes the failure through the Result seam and reports it as a compaction
///     failure).</summary>
public class SessionHandoffPreferencesTests
{
  [Fact]
  public void TryParse_Null_BindsFalse() => Assert.False(SessionHandoffPreferences.TryParse(null).Value);

  [Fact]
  public void TryParse_FalseExact_BindsFalse() => Assert.False(SessionHandoffPreferences.TryParse("false").Value);

  [Fact]
  public void TryParse_TrueExact_BindsTrue() => Assert.True(SessionHandoffPreferences.TryParse("true").Value);

  [Theory]
  [InlineData("TRUE")]
  [InlineData("True")]
  [InlineData("FALSE")]
  [InlineData("yes")]
  [InlineData("1")]
  [InlineData(" ")]
  public void TryParse_AnythingElse_IsATypedFailureNamingTheKey(string stored)
  {
    Result<bool> parsed = SessionHandoffPreferences.TryParse(stored);
    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidPreferenceValue", parsed.Error.Code);
    Assert.Contains(SessionHandoffPreferences.Key, parsed.Error.Message, StringComparison.Ordinal);
  }
}
