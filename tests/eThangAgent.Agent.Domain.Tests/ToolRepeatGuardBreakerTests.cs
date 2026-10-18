namespace eThangAgent.AgentDomain.Tests;

/// <summary>Breaker policy (2026-10-04 incident): a model that VARIES broken arguments
///     never trips the identical-args nudges — ~200 failed exec calls each looked like a
///     fresh call. The breaker counts consecutive failures of the SAME tool across ANY
///     arguments, trips a stronger nudge at 3, and escalates to the CRITICAL error at 13
///     (the loop ends the turn on the next breaker-counted call — see
///     ToolRepeatGuardCriticalTests).</summary>
public class ToolRepeatGuardBreakerTests
{
  private const string Error = "Error [ExecParseError]: program failed validation.";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  [Fact]
  public void ThreeConsecutiveFailures_VaryingArguments_TripBreakerNudge()
  {
    ToolRepeatGuard guard = new();

    GuardOutcome nudge = GuardOutcome.Quiet;
    for (int i = 1; i <= 3; i++)
    {
      nudge = guard.Observe("exec", Args(i), true, Error);
    }

    Assert.NotNull(nudge.NudgeLine);
    Assert.StartsWith("[repeat guard]", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("'exec'", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("3 consecutive", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("any arguments", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.False(nudge.Critical);
    Assert.False(nudge.EndTurn);
  }

  [Fact]
  public void OneAndTwoFailures_NoBreakerNudge()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), true, Error);
    GuardOutcome second = guard.Observe("exec", Args(2), true, Error);

    // 2nd failure with different args: no BREAKER nudge (identical-args streak is 1).
    Assert.Null(second.NudgeLine);
  }

  [Fact]
  public void SuccessOnAnyCall_ResetsBreaker()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), true, Error);
    _ = guard.Observe("exec", Args(2), true, Error);
    _ = guard.Observe("exec", Args(3), false, "ok");

    // Two more failures after the success: the breaker streak is 2, below 3.
    _ = guard.Observe("exec", Args(4), true, Error);
    GuardOutcome nudge = guard.Observe("exec", Args(5), true, Error);

    Assert.Null(nudge.NudgeLine);
  }

  [Fact]
  public void DifferentToolName_StartsItsOwnBreakerCount()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), true, Error);
    _ = guard.Observe("exec", Args(2), true, Error);

    GuardOutcome nudge = guard.Observe("read", Args(3), true, Error);

    Assert.Null(nudge.NudgeLine);
  }

  [Fact]
  public void ThirteenConsecutiveFailures_CriticalOutcomeFires()
  {
    ToolRepeatGuard guard = new();
    GuardOutcome last = GuardOutcome.Quiet;
    for (int i = 1; i <= 13; i++)
    {
      last = guard.Observe("exec", Args(i), true, Error);
    }

    Assert.True(last.Critical);
    Assert.False(last.EndTurn);
    Assert.NotNull(last.NudgeLine);
    Assert.Contains("13 consecutive", last.NudgeLine, StringComparison.Ordinal);
    Assert.DoesNotContain("suspended", last.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Reset_ClearsCriticalTracking()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("exec", Args(i), true, Error);
    }

    guard.Reset();

    GuardOutcome after = guard.Observe("exec", Args(14), true, Error);
    Assert.False(after.EndTurn);
    Assert.False(after.Critical);
  }
}
