namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #303 D1/D2/D7: the retry rule and the aggregate's re-entry of a failed step.
///     Fakes only - no store, no clock, no runtime.</summary>
public class ChainRetryPolicyTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  private static Chain NewChain(params int[] positions) => Chain.New("C:\\ws", 3, 1, "t",
      "session-root", positions.Length == 0 ? [1] : positions, T0);

  /// <summary>A chain whose step 1 was claimed by session-a <paramref name="attempts"/> times
  ///     and then failed. Each re-claim needs a lapsed window, so the clock advances between
  ///     them - which is also how a real second attempt happens.</summary>
  private static Chain Failed(int attempts = 1)
  {
    Chain chain = NewChain(1, 2);
    DateTimeOffset at = T0;
    for (int i = 0; i < attempts; i++)
    {
      chain = chain.ClaimStep(1, "session-a", at);
      at += ChainClaimPolicy.ClaimTtl + TimeSpan.FromMinutes(1);
    }

    return chain.SettleStep(1, ChainStepStatus.Failed, ChainActor.Session("session-a"),
        "provider blip", at);
  }

  [Fact]
  public void MaxAttempts_IsThree_AndIsANamedConstantNotASetting()
      => Assert.Equal(3, ChainRetryPolicy.MaxAttempts);

  [Theory]
  [InlineData(0, true)]
  [InlineData(1, true)]
  [InlineData(2, true)]
  [InlineData(3, false)]
  [InlineData(4, false)]
  public void CanRetry_BoundaryIsTheCapItself(int attempts, bool expected)
      => Assert.Equal(expected, ChainRetryPolicy.CanRetry(attempts));

  [Fact]
  public void Decide_UnderTheCap_Retries_AtTheCap_Parks()
  {
    Assert.Equal(ChainRetryDecision.Retry, ChainRetryPolicy.Decide(2));
    Assert.Equal(ChainRetryDecision.Park, ChainRetryPolicy.Decide(3));
  }

  [Fact]
  public void FailureNote_NamesTheAttemptTheChainAndTheStep_AndTheFailure()
  {
    string note = ChainRetryPolicy.FailureNote(7, 2, 2, "provider returned 503");
    Assert.Contains("attempt 2 of 3", note, StringComparison.Ordinal);
    Assert.Contains("chain #7 step 2", note, StringComparison.Ordinal);
    Assert.Contains("provider returned 503", note, StringComparison.Ordinal);
    Assert.Contains("RESUMED", note, StringComparison.Ordinal);
  }

  [Fact]
  public void FailureNote_WithNoRecordedFailure_SaysSoRatherThanLeavingAGap()
  {
    string note = ChainRetryPolicy.FailureNote(1, 1, 1, null);
    Assert.Contains("without recording a reason", note, StringComparison.Ordinal);
  }

  [Fact]
  public void FailureNote_IsNotTheWatchdogNudge()
  {
    // D7: the nudge is about IDLENESS; reusing it would tell a session something untrue
    // about why it was restarted.
    string note = ChainRetryPolicy.FailureNote(1, 1, 1, "boom");
    Assert.NotEqual(RootSupervisionPolicy.Nudge(1, 1), note);
    Assert.DoesNotContain("idle", note, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void RetryStep_ReEntersTheFailedStepForTheSameSession_KeepingTheAttemptCount()
  {
    Chain chain = Failed(attempts: 2);
    Chain retried = chain.RetryStep(1, "session-a", T0.AddMinutes(5));
    ChainStep step = retried.Steps[0];
    Assert.Equal(ChainStepStatus.Running, step.Status);
    Assert.Equal("session-a", step.SessionId);
    Assert.Equal(2, step.Attempts); // a retry is not a new attempt
    Assert.True(step.ClaimedAt is not null, "a retried step must hold a live claim clock");
    Assert.Null(step.CompletedAt);
  }

  [Fact]
  public void RetryStep_ForADifferentSession_IsRefused()
  {
    // D1: a retry resumes THAT session. A different one has no transcript to continue.
    Chain chain = Failed();
    ChainInputException ex = Assert.Throws<ChainInputException>(
        () => chain.RetryStep(1, "session-b", T0));
    Assert.Contains("session-a", ex.Message, StringComparison.Ordinal);
    Assert.Contains("SAME session", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RetryStep_OnAStepThatDidNotFail_IsRefused()
  {
    Chain chain = NewChain(1);
    ChainInputException ex = Assert.Throws<ChainInputException>(
        () => chain.RetryStep(1, "session-a", T0));
    Assert.Contains("not Failed", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void RetryStep_OnATerminalChain_IsRefused()
  {
    Chain chain = Failed().SetStatus(ChainStatus.Abandoned, ChainActor.User, T0);
    _ = Assert.Throws<ChainInputException>(() => chain.RetryStep(1, "session-a", T0));
  }

  [Fact]
  public void RetriedStep_IsNoLongerAvailableToAnotherSession()
  {
    // The re-entered step holds a live claim, so the walker will not hand it to anyone else.
    Chain retried = Failed().RetryStep(1, "session-a", T0.AddMinutes(5));
    Assert.False(retried.Steps[0].IsAvailable(T0.AddMinutes(6)));
    Assert.Equal(ChainStepStatus.Pending, retried.NextAvailableStep(T0.AddMinutes(6))?.Status);
    Assert.Equal(2, retried.NextAvailableStep(T0.AddMinutes(6))?.Position);
  }
}
