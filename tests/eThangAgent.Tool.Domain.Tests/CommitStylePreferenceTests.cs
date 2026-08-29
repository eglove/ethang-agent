using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The commit style is app-scoped host state the git_commit tool resolves at
/// execution time — never a model-facing parameter. These tests pin the pure resolution
/// semantics the host adapter depends on: unset (null/empty) means the documented
/// Conventional default, the three exact names round-trip, and an unrecognized stored
/// value is a typed error — never a silent fallback.</summary>
public class CommitStylePreferenceTests
{
  [Theory]
  [InlineData(null)]
  [InlineData("")]
  public void Resolve_Unset_ConventionalDefault(string? stored)
  {
    Result<CommitStyle> r = CommitStylePreference.Resolve(stored);
    Assert.True(r.IsSuccess);
    Assert.Equal(CommitStyle.Conventional, r.Value);
  }

  [Theory]
  [InlineData("Conventional", CommitStyle.Conventional)]
  [InlineData("Gitmoji", CommitStyle.Gitmoji)]
  [InlineData("None", CommitStyle.None)]
  public void Resolve_ExactStoredName_RoundTrips(string stored, CommitStyle expected)
  {
    Result<CommitStyle> r = CommitStylePreference.Resolve(stored);
    Assert.True(r.IsSuccess);
    Assert.Equal(expected, r.Value);
  }

  [Theory]
  [InlineData("gitmoji")]
  [InlineData("none ")]
  [InlineData("bogus")]
  public void Resolve_UnknownStoredValue_TypedErrorNotDefault(string stored)
  {
    Result<CommitStyle> r = CommitStylePreference.Resolve(stored);
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal("InvalidStoredStyle", error.Code);
    Assert.Contains(stored, error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Resolve_Error_MessageNamesAllLegalValues()
  {
    Result<CommitStyle> r = CommitStylePreference.Resolve("bogus");
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Multiple(
        () => Assert.Contains("Conventional", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("Gitmoji", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("None", error.Message, StringComparison.Ordinal));
  }
}
