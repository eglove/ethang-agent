namespace eThangAgent.AgentDomain.Tests;

/// <summary>Success-repetition policy (2026-10-08 incident): a model that re-issues one
///     byte-identical call and gets a SUCCESSFUL result every time was invisible to the
///     failure-fed guard — a depth-1 subagent looped one exec call 235 times over 2.5
///     hours. The guard counts consecutive identical-argument SUCCESSES: a nudge at the
///     3rd, the CRITICAL error at the 13th (the loop ends the turn on the next
///     breaker-counted call — see ToolRepeatGuardCriticalTests). Any different call or
///     different arguments resets the streak, so legitimate polling is not punished.</summary>
public class ToolRepeatGuardSuccessTests
{
  private const string Result = "the same 2843-char payload";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  [Fact]
  public void TwoIdenticalSuccesses_NoNudge()
  {
    ToolRepeatGuard guard = new();

    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result).NudgeLine);
    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result).NudgeLine);
  }

  [Fact]
  public void ThreeIdenticalSuccesses_NudgesAtThird()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    GuardOutcome nudge = guard.Observe("exec", Args(1), isError: false, Result);

    Assert.NotNull(nudge.NudgeLine);
    Assert.StartsWith("[repeat guard]", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("'exec'", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("3 consecutive times", nudge.NudgeLine, StringComparison.Ordinal);
    // Distinct from the failure nudges: it names the SUCCESS repetition.
    Assert.DoesNotContain("failed", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.False(nudge.Critical);
    Assert.False(nudge.EndTurn);
  }

  [Fact]
  public void DifferentArguments_ResetsSuccessStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    // Different arguments: a fresh call — the streak restarts at 1.
    Assert.Null(guard.Observe("exec", Args(2), isError: false, Result).NudgeLine);
    Assert.Null(guard.Observe("exec", Args(2), isError: false, Result).NudgeLine);
  }

  [Fact]
  public void DifferentTool_ResetsSuccessStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    Assert.Null(guard.Observe("read", Args(1), isError: false, Result).NudgeLine);
    Assert.Null(guard.Observe("read", Args(1), isError: false, Result).NudgeLine);
  }

  [Fact]
  public void SingleSuccess_NoNudge()
  {
    ToolRepeatGuard guard = new();

    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result).NudgeLine);
  }

  [Fact]
  public void DistinctCalls_NoNudge()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 20; i++)
    {
      Assert.Null(guard.Observe("exec", Args(i), isError: false, Result).NudgeLine);
    }
  }

  [Fact]
  public void ThirteenthIdenticalSuccess_CriticalOutcome()
  {
    ToolRepeatGuard guard = new();
    GuardOutcome last = GuardOutcome.Quiet;
    for (int i = 1; i <= 13; i++)
    {
      last = guard.Observe("exec", Args(1), isError: false, Result);
    }

    Assert.True(last.Critical);
    Assert.False(last.EndTurn);
    Assert.NotNull(last.NudgeLine);
    Assert.DoesNotContain("suspended", last.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Failure_BreaksSuccessStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    // A failure of the same call resets the success streak.
    _ = guard.Observe("exec", Args(1), isError: true, "Error [Boom]: nope");

    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result).NudgeLine);
    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result).NudgeLine);
  }

  [Fact]
  public void Reset_ClearsSuccessCriticalTracking()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("exec", Args(1), isError: false, Result);
    }

    guard.Reset();

    GuardOutcome after = guard.Observe("exec", Args(1), isError: false, Result);
    Assert.False(after.EndTurn);
    Assert.False(after.Critical);
  }
}
