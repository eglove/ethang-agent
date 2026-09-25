namespace eThangAgent.AgentDomain.Tests;

/// <summary>Pure policy: identical tool calls failing in a row trip a "[repeat guard]"
///     System nudge at the 2nd failure (and again at the 5th); any success or a
///     different call resets the streak.</summary>
public class ToolRepeatGuardTests
{
  private const string Error = "Error [MissingParameter]: Missing required parameter 'timeoutSeconds'.";

  [Fact]
  public void FirstFailure_NoNudge()
  {
    ToolRepeatGuard guard = new();

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: true, Error));
  }

  [Fact]
  public void SecondIdenticalFailure_NudgesWithStreakAndErrorHead()
  {
    ToolRepeatGuard guard = new();

    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: true, Error);
    string? nudge = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: true, Error);

    Assert.NotNull(nudge);
    Assert.StartsWith("[repeat guard]", nudge, StringComparison.Ordinal);
    Assert.Contains("'read'", nudge, StringComparison.Ordinal);
    Assert.Contains("failed 2 consecutive times", nudge, StringComparison.Ordinal);
    Assert.Contains(Error, nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void ThirdAndFourthFailures_NoFurtherNudge()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error));
    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error));
  }

  [Fact]
  public void FifthFailure_SecondNudge()
  {
    ToolRepeatGuard guard = new();
    for (int i = 0; i < 4; i++)
    {
      _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    }

    string? nudge = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.NotNull(nudge);
    Assert.Contains("failed 5 consecutive times", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void SuccessOnSameCall_ResetsStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", isError: false, "file content"));
    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error)); // fresh streak: 1
    string? nudge = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.NotNull(nudge);
    Assert.Contains("failed 2 consecutive times", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void DifferentArguments_TreatedAsDifferentCall()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":2}", true, Error));
  }

  [Fact]
  public void DifferentArguments_InterruptsStreak_SameArgsAgainStartsFresh()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error);
    _ = guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error); // nudged
    _ = guard.Observe("write", /*lang=json,strict*/ "{\"p\":1}", true, Error); // different call

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{\"p\":1}", true, Error)); // streak restarts at 1
  }

  [Fact]
  public void MultilineError_NudgeQuotesFirstLineOnly()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{}", true, "Error [Boom]: first line\r\nsecond line");
    string? nudge = guard.Observe("read", /*lang=json,strict*/ "{}", true, "Error [Boom]: first line\r\nsecond line");

    Assert.NotNull(nudge);
    Assert.Contains("(Error [Boom]: first line)", nudge, StringComparison.Ordinal);
    Assert.DoesNotContain("second line", nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void OverlongErrorHead_TruncatedWithEllipsis()
  {
    const string prefix = "Error [Boom]: ";
    string longError = prefix + new string('x', 300);
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{}", true, longError);
    string? nudge = guard.Observe("read", /*lang=json,strict*/ "{}", true, longError);

    Assert.NotNull(nudge);
    string expectedHead = prefix + new string('x', ToolRepeatGuard.MaxErrorHeadLength - prefix.Length) + "…";
    Assert.Contains(expectedHead, nudge, StringComparison.Ordinal);
  }

  [Fact]
  public void Reset_ClearsStreak()
  {
    ToolRepeatGuard guard = new();
    _ = guard.Observe("read", /*lang=json,strict*/ "{}", true, Error);
    guard.Reset();

    Assert.Null(guard.Observe("read", /*lang=json,strict*/ "{}", true, Error));
  }
}
