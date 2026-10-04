namespace eThangAgent.AgentDomain.Tests;

/// <summary>Breaker policy (2026-10-04 incident): a model that VARIES broken arguments
///     never trips the identical-args nudges — ~200 failed exec calls each looked like a
///     fresh call. The breaker counts consecutive failures of the SAME tool across ANY
///     arguments, trips a stronger nudge at 3, and re-arms at +10 with escalated wording
///     (the loop then refuses the tool for the turn's remainder).</summary>
public class ToolRepeatGuardBreakerTests
{
  private const string Error = "Error [ExecParseError]: program failed validation.";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  [Fact]
  public void ThreeConsecutiveFailures_VaryingArguments_TripBreakerNudge()
  {
    ToolRepeatGuard guard = new();

    string? nudge = null;
    for (int i = 1; i <= 3; i++)
    {
      nudge = guard.Observe("exec", Args(i), true, Error);
    }

    Assert.NotNull(nudge);
    Assert.StartsWith("[repeat guard]", nudge, StringComparison.Ordinal);
    Assert.Contains("'exec'", nudge, StringComparison.Ordinal);
    Assert.Contains("3 consecutive", nudge, StringComparison.Ordinal);
    Assert.Contains("any arguments", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void OneAndTwoFailures_NoBreakerNudge()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), true, Error);
    string? second = guard.Observe("exec", Args(2), true, Error);

    // 2nd failure with different args: no BREAKER nudge (identical-args streak is 1).
    Assert.Null(second);
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
    string? nudge = guard.Observe("exec", Args(5), true, Error);

    Assert.Null(nudge);
  }

  [Fact]
  public void DifferentToolName_StartsItsOwnBreakerCount()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("exec", Args(1), true, Error);
    _ = guard.Observe("exec", Args(2), true, Error);

    string? nudge = guard.Observe("read", Args(3), true, Error);

    Assert.Null(nudge);
  }

  [Fact]
  public void ThirteenConsecutiveFailures_ReArmNudgeFiresWithEscalation()
  {
    ToolRepeatGuard guard = new();
    string? reArm = null;
    for (int i = 1; i <= 13; i++)
    {
      reArm = guard.Observe("exec", Args(i), true, Error);
    }

    Assert.NotNull(reArm);
    Assert.Contains("13 consecutive", reArm, StringComparison.Ordinal);
    Assert.Contains("suspended", reArm, StringComparison.Ordinal);
  }

  [Fact]
  public void ThirteenConsecutiveFailures_GuardSuspendsTool()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("exec", Args(i), true, Error);
    }

    Assert.True(guard.IsSuspended("exec"));
    Assert.False(guard.IsSuspended("read"));
  }

  [Fact]
  public void Reset_ClearsSuspension()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("exec", Args(i), true, Error);
    }

    guard.Reset();

    Assert.False(guard.IsSuspended("exec"));
  }
}
