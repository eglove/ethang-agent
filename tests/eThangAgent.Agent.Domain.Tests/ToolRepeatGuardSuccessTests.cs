namespace eThangAgent.AgentDomain.Tests;

/// <summary>Success-repetition policy (2026-10-08 incident): a model that re-issues one
///     byte-identical call and gets a SUCCESSFUL result every time was invisible to the
///     failure-fed guard — a depth-1 subagent looped one exec call 235 times over 2.5
///     hours. The guard now counts consecutive identical-argument SUCCESSES: a nudge at
///     the 3rd, suspension of the tool at the 13th (mirroring the failure breaker). Any
///     different call or different arguments resets the streak, so legitimate polling is
///     not punished.</summary>
public class ToolRepeatGuardSuccessTests
{
  private const string Result = "the same 2843-char payload";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  [Fact]
  public void TwoIdenticalSuccesses_NoNudge()
  {
    ToolRepeatGuard guard = new();

    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result));
    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result));
  }

  [Fact]
  public void ThreeIdenticalSuccesses_NudgesAtThird()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    string? nudge = guard.Observe("exec", Args(1), isError: false, Result);

    Assert.NotNull(nudge);
    Assert.StartsWith("[repeat guard]", nudge, StringComparison.Ordinal);
    Assert.Contains("'exec'", nudge, StringComparison.Ordinal);
    Assert.Contains("3 consecutive times", nudge, StringComparison.Ordinal);
    // Distinct from the failure nudges: it names the SUCCESS repetition.
    Assert.DoesNotContain("failed", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void DifferentArguments_ResetsSuccessStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    // Different arguments: a fresh call — the streak restarts at 1.
    Assert.Null(guard.Observe("exec", Args(2), isError: false, Result));
    Assert.Null(guard.Observe("exec", Args(2), isError: false, Result));
  }

  [Fact]
  public void DifferentTool_ResetsSuccessStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    Assert.Null(guard.Observe("read", Args(1), isError: false, Result));
    Assert.Null(guard.Observe("read", Args(1), isError: false, Result));
  }

  [Fact]
  public void SingleSuccess_NoNudge()
  {
    ToolRepeatGuard guard = new();

    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result));
  }

  [Fact]
  public void DistinctCalls_NoNudge()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 20; i++)
    {
      Assert.Null(guard.Observe("exec", Args(i), isError: false, Result));
    }
  }

  [Fact]
  public void SuccessStreakSuspendsToolAtThreshold()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i < 13; i++)
    {
      _ = guard.Observe("exec", Args(1), isError: false, Result);
    }

    Assert.False(guard.IsSuspended("exec"));

    _ = guard.Observe("exec", Args(1), isError: false, Result);

    Assert.True(guard.IsSuspended("exec"));
    Assert.False(guard.IsSuspended("read"));
  }

  [Fact]
  public void SuccessSuspensionNudge_NamesSuspension()
  {
    ToolRepeatGuard guard = new();
    string? last = null;
    for (int i = 0; i < 13; i++)
    {
      last = guard.Observe("exec", Args(1), isError: false, Result);
    }

    Assert.NotNull(last);
    Assert.Contains("suspended", last, StringComparison.Ordinal);
  }

  [Fact]
  public void Failure_BreaksSuccessStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), isError: false, Result);
    _ = guard.Observe("exec", Args(1), isError: false, Result);

    // A failure of the same call resets the success streak.
    _ = guard.Observe("exec", Args(1), isError: true, "Error [Boom]: nope");

    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result));
    Assert.Null(guard.Observe("exec", Args(1), isError: false, Result));
  }

  [Fact]
  public void Reset_ClearsSuccessSuspension()
  {
    ToolRepeatGuard guard = new();
    for (int i = 0; i < 13; i++)
    {
      _ = guard.Observe("exec", Args(1), isError: false, Result);
    }

    guard.Reset();

    Assert.False(guard.IsSuspended("exec"));
  }
}
