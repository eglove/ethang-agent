namespace eThangAgent.AgentDomain.Tests;

/// <summary>Critical escalation policy: at the 13th breaker-counted call (failure
///     breaker OR success breaker) the guard returns a CRITICAL outcome the model must
///     address; the NEXT breaker-counted call ends the turn. Suspension is deleted —
///     no code path may suspend a tool again.</summary>
public class ToolRepeatGuardCriticalTests
{
  private const string Error = "Error [ExecParseError]: program failed validation.";

  private static string Args(int i) => "{\"program\":\"p" + i + "\"}";

  [Fact]
  public void ThirteenthIdenticalFailure_CriticalLine_NoSuspensionEver()
  {
    ToolRepeatGuard guard = new();
    GuardOutcome last = GuardOutcome.Quiet;
    for (int i = 1; i <= 13; i++)
    {
      last = guard.Observe("read", "{}", true, Error);
    }

    Assert.True(last.Critical);
    Assert.False(last.EndTurn);
    Assert.NotNull(last.NudgeLine);
    Assert.StartsWith("[repeat guard]", last.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("critical error", last.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("13 consecutive", last.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("end your turn", last.NudgeLine, StringComparison.Ordinal);
    Assert.DoesNotContain("suspended", last.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void FourteenthIdenticalFailure_EndTurn_NoNudgeLine()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("read", "{}", true, Error);
    }

    GuardOutcome fourteenth = guard.Observe("read", "{}", true, Error);

    Assert.True(fourteenth.EndTurn);
    Assert.Null(fourteenth.NudgeLine);
  }

  [Fact]
  public void SuccessBreaker_ThirteenthCritical_FourteenthEndTurn()
  {
    ToolRepeatGuard guard = new();
    GuardOutcome last = GuardOutcome.Quiet;
    for (int i = 1; i <= 13; i++)
    {
      last = guard.Observe("exec", "{}", false, "the same payload");
    }

    Assert.True(last.Critical);
    Assert.False(last.EndTurn);
    Assert.NotNull(last.NudgeLine);
    Assert.DoesNotContain("suspended", last.NudgeLine, StringComparison.Ordinal);

    GuardOutcome fourteenth = guard.Observe("exec", "{}", false, "the same payload");
    Assert.True(fourteenth.EndTurn);
    Assert.Null(fourteenth.NudgeLine);
  }

  [Fact]
  public void FailureBreaker_VariedArguments_CriticalThenEndTurn()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 12; i++)
    {
      _ = guard.Observe("exec", Args(i), true, Error);
    }

    GuardOutcome thirteenth = guard.Observe("exec", Args(13), true, Error);
    Assert.True(thirteenth.Critical);
    Assert.False(thirteenth.EndTurn);

    GuardOutcome fourteenth = guard.Observe("exec", Args(14), true, Error);
    Assert.True(fourteenth.EndTurn);
    Assert.Null(fourteenth.NudgeLine);
  }

  [Fact]
  public void DifferentTool_AfterCritical_ResetsCriticalTracking()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("exec", Args(i), true, Error);
    }

    GuardOutcome other = guard.Observe("read", Args(1), true, Error);
    Assert.False(other.EndTurn);

    GuardOutcome again = guard.Observe("read", Args(2), true, Error);
    Assert.False(again.EndTurn);
    Assert.False(again.Critical);
  }

  [Fact]
  public void Success_AfterCritical_ResetsFailureBreakerCritical()
  {
    ToolRepeatGuard guard = new();
    for (int i = 1; i <= 13; i++)
    {
      _ = guard.Observe("exec", Args(i), true, Error);
    }

    _ = guard.Observe("exec", Args(14), false, "ok");
    GuardOutcome after = guard.Observe("exec", Args(15), true, Error);
    Assert.False(after.EndTurn);
    Assert.False(after.Critical);
  }

  [Fact]
  public void Reset_ClearsCriticalFiredState()
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
