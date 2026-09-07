using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Worktree names gate every git worktree path and branch this capability
///     creates, so the validation contract is pinned here exhaustively: lowercase
///     ASCII letters, digits, and hyphens only (^[a-z0-9-]+$), length 1..64, no
///     trimming — whitespace and near-misses are rejected, never cleaned up. Every
///     failure carries the exact code InvalidName and a message naming the rule and
///     the pattern.</summary>
public class WorktreeNameTests
{
  // ---- Accepted: the ^[a-z0-9-]+$ shape, length 1..64 ----

  [Theory]
  [InlineData("agent-fix")]
  [InlineData("a")]
  [InlineData("review-worktree-for-gate-hardening-2026-07-21-a1b2c3")] // 64 chars
  public void Create_ValidLowercaseHyphenName_Succeeds(string raw)
  {
    Result<WorktreeName> r = WorktreeName.Create(raw);
    Assert.True(r.IsSuccess);
    Assert.Equal(raw, r.Value.Value);
  }

  [Fact]
  public void Create_SixtyFourCharacterName_IsAcceptedAtTheUpperBound()
  {
    string raw = new('a', 64);
    Result<WorktreeName> r = WorktreeName.Create(raw);
    Assert.True(r.IsSuccess);
    Assert.Equal(64, r.Value.Value.Length);
  }

  // ---- Rejected: every rule breach is the InvalidName code ----

  [Theory]
  [InlineData("Agent")]        // uppercase
  [InlineData("has space")]    // space
  [InlineData("has/slash")]    // path separator
  [InlineData("has_under")]    // underscore
  [InlineData("")]             // empty
  [InlineData(" ")]            // whitespace
  [InlineData("agent-fix ")]   // trailing whitespace — no trimming
  [InlineData(" agent-fix")]   // leading whitespace — no trimming
  [InlineData("é")]            // non-ASCII letter
  public void Create_RuleBreach_FailsWithInvalidName(string raw)
  {
    Result<WorktreeName> r = WorktreeName.Create(raw);
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal("InvalidName", error.Code);
  }

  [Fact]
  public void Create_SixtyFiveCharacterName_ExceedsTheUpperBound()
  {
    Result<WorktreeName> r = WorktreeName.Create(new('a', 65));
    Assert.False(r.IsSuccess);
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Equal("InvalidName", error.Code);
  }

  [Fact]
  public void Create_Error_MessageNamesTheRuleAndThePattern()
  {
    Result<WorktreeName> r = WorktreeName.Create("Agent");
    DomainError error = r.Error ?? throw new InvalidOperationException("expected failure carried no error");
    Assert.Multiple(
        () => Assert.Contains("a-z0-9-", error.Message, StringComparison.Ordinal),
        () => Assert.Contains("64", error.Message, StringComparison.Ordinal));
  }
}
