namespace eThangAgent.AgentDomain.Tests;

/// <summary>Identical-args failure nudges: the same tool call failing in a row trips a
///     "[repeat guard]" System nudge at the 2nd failure (and again at the 5th); any
///     success or a different call resets the streak.</summary>
public class ToolRepeatGuardTests
{
  private const string Error = "Error [MissingParameter]: Missing required parameter 'timeoutSeconds'.";

  [Fact]
  public void FirstFailure_NoNudge()
  {
    ToolRepeatGuard guard = new();

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: true, Error).NudgeLine);
  }

  [Fact]
  public void SecondIdenticalFailure_NudgesWithStreakAndErrorHead()
  {
    ToolRepeatGuard guard = new();

    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: true, Error);
    GuardOutcome nudge = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: true, Error);

    Assert.NotNull(nudge.NudgeLine);
    Assert.StartsWith("[repeat guard]", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("'read'", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains("failed 2 consecutive times", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.Contains(Error, nudge.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void ThirdIdenticalFailure_BreakerNudgeSupersedesSilence()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    // The 3rd failure fires the BREAKER nudge (cross-argument policy), not silence:
    // the identical-args nudge fired at 2, the breaker takes over at 3.
    GuardOutcome breaker = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    Assert.NotNull(breaker.NudgeLine);
    Assert.Contains("any arguments", breaker.NudgeLine, StringComparison.Ordinal);
    // The 4th identical failure: no further nudge from either policy.
    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error).NudgeLine);
  }

  [Fact]
  public void FifthFailure_SecondNudge()
  {
    ToolRepeatGuard guard = new();
    for (int i = 0; i < 4; i++)
    {
      _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    }

    GuardOutcome nudge = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.NotNull(nudge.NudgeLine);
    Assert.Contains("failed 5 consecutive times", nudge.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void SuccessOnSameCall_ResetsStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: false, "file content").NudgeLine);
    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error).NudgeLine); // fresh streak: 1
    GuardOutcome nudge = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.NotNull(nudge.NudgeLine);
    Assert.Contains("failed 2 consecutive times", nudge.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void DifferentArguments_TreatedAsDifferentCall()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":2}", true, Error).NudgeLine);
  }

  [Fact]
  public void DifferentArguments_InterruptsStreak_SameArgsAgainStartsFresh()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error); // nudged
    _ = guard.Observe("write", /*lang=json,strict*/ "{\"p\":1}", true, Error); // different call

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error).NudgeLine); // streak restarts at 1
  }

  [Fact]
  public void MultilineError_NudgeQuotesFirstLineOnly()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{}", true, "Error [Boom]: first line\r\nsecond line");
    GuardOutcome nudge = guard.Observe("read", /*lang=json,strict*/ "{}", true, "Error [Boom]: first line\r\nsecond line");

    Assert.NotNull(nudge.NudgeLine);
    Assert.Contains("(Error [Boom]: first line)", nudge.NudgeLine, StringComparison.Ordinal);
    Assert.DoesNotContain("second line", nudge.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void OverlongErrorHead_TruncatedWithEllipsis()
  {
    const string prefix = "Error [Boom]: ";
    string longError = prefix + new string('x', 300);
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{}", true, longError);
    GuardOutcome nudge = guard.Observe("read", /*lang=json,strict*/ "{}", true, longError);

    Assert.NotNull(nudge.NudgeLine);
    string expectedHead = prefix + new string('x', ToolRepeatGuard.MaxErrorHeadLength - prefix.Length) + "…";
    Assert.Contains(expectedHead, nudge.NudgeLine, StringComparison.Ordinal);
  }

  [Fact]
  public void Reset_ClearsStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{}", true, Error);
    guard.Reset();

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{}", true, Error).NudgeLine);
  }
}
