namespace eThangAgent.AgentDomain.Tests;

/// <summary>The two code sets and the schedule a turn's transient-fault retries are held
///     to. The domain decides from the CODE alone, so a code in neither set keeps
///     whatever handling it had.</summary>
public class TurnRetryPolicyTests
{
  [Theory]
  [InlineData("StreamInterrupted")]
  [InlineData("ProviderError")]
  [InlineData("ProviderTimeout")]
  public void IsTransient_AcceptsExactlyTheTransientCodes(string code) =>
      Assert.True(TurnRetryPolicy.IsTransient(code));

  [Theory]
  [InlineData("RateLimited")]
  [InlineData("ModelRejected")]
  [InlineData("ContextWindowExceeded")]
  [InlineData("TurnCancelled")]
  [InlineData("HostUnavailable")]
  [InlineData("RepeatGuardStop")]
  [InlineData("InvalidSeed")]
  [InlineData("")]
  public void IsTransient_RejectsEveryOtherCode(string code) =>
      Assert.False(TurnRetryPolicy.IsTransient(code));

  [Theory]
  [InlineData("RateLimited")]
  [InlineData("ModelRejected")]
  public void IsModelVerdict_AcceptsExactlyTheModelVerdictCodes(string code) =>
      Assert.True(TurnRetryPolicy.IsModelVerdict(code));

  [Theory]
  [InlineData("StreamInterrupted")]
  [InlineData("ProviderError")]
  [InlineData("ProviderTimeout")]
  [InlineData("ContextWindowExceeded")]
  [InlineData("TurnCancelled")]
  [InlineData("HostUnavailable")]
  [InlineData("")]
  public void IsModelVerdict_RejectsEveryOtherCode(string code) =>
      Assert.False(TurnRetryPolicy.IsModelVerdict(code));

  // A MODEL VERDICT IS NEVER TRANSIENT, AND A TRANSIENT FAULT IS NEVER A MODEL VERDICT:
  // the two sets partition the codes they own, so no failure is both.
  [Theory]
  [InlineData("StreamInterrupted")]
  [InlineData("ProviderError")]
  [InlineData("ProviderTimeout")]
  [InlineData("RateLimited")]
  [InlineData("ModelRejected")]
  public void TheTwoSets_DoNotOverlap(string code) =>
      Assert.NotEqual(TurnRetryPolicy.IsTransient(code), TurnRetryPolicy.IsModelVerdict(code));

  // A code in NEITHER set keeps its own handling: the domain retries nothing and drops no
  // model on it.
  [Fact]
  public void ContextWindowExceeded_IsInNeitherSet()
  {
    Assert.False(TurnRetryPolicy.IsTransient("ContextWindowExceeded"));
    Assert.False(TurnRetryPolicy.IsModelVerdict("ContextWindowExceeded"));
    Assert.False(TurnRetryPolicy.IsDropVerdict("ContextWindowExceeded"));
  }

  /// <summary>The two names answer for one set: the turn loop decides from IsDropVerdict and
  ///     the app's own list decides from IsModelVerdict, so a code one accepted and the other
  ///     rejected would let one side drop a model the other kept.</summary>
  [Theory]
  [InlineData("RateLimited")]
  [InlineData("ModelRejected")]
  [InlineData("ProviderUnauthorized")]
  [InlineData("ProviderError")]
  [InlineData("ProviderTimeout")]
  [InlineData("StreamInterrupted")]
  [InlineData("ContextWindowExceeded")]
  [InlineData("")]
  public void IsDropVerdict_AndIsModelVerdict_AreOneAnswer(string code) =>
      Assert.Equal(TurnRetryPolicy.IsDropVerdict(code), TurnRetryPolicy.IsModelVerdict(code));

  /// <summary>A refused credential is a configuration fault about the account, not a verdict
  ///     about the model: the turn must stop and report it, and the app must not drop the model.
  ///     </summary>
  [Fact]
  public void ProviderUnauthorized_IsNeitherRetriedNorDropped()
  {
    Assert.False(TurnRetryPolicy.IsTransient("ProviderUnauthorized"));
    Assert.False(TurnRetryPolicy.IsDropVerdict("ProviderUnauthorized"));
    Assert.False(TurnRetryPolicy.IsModelVerdict("ProviderUnauthorized"));
  }

  /// <summary>The drop set is exactly the two codes the walk advances on, so a code added to
  ///     one of them cannot silently become a third drop reason.</summary>
  [Fact]
  public void IsDropVerdict_AcceptsExactlyRateLimitedAndModelRejected()
  {
    Assert.True(TurnRetryPolicy.IsDropVerdict("RateLimited"));
    Assert.True(TurnRetryPolicy.IsDropVerdict("ModelRejected"));
    Assert.False(TurnRetryPolicy.IsDropVerdict("ProviderUnauthorized"));
    Assert.False(TurnRetryPolicy.IsDropVerdict("StreamInterrupted"));
  }

  [Fact]
  public void TheSchedule_IsSixAttemptsOfOneSecondDoublingToThirty()
  {
    Assert.Equal(6, TurnRetryPolicy.MaxAttempts);
    Assert.Equal(TimeSpan.FromSeconds(1), TurnRetryPolicy.BaseDelay);
    Assert.Equal(TimeSpan.FromSeconds(30), TurnRetryPolicy.MaxDelay);
    Assert.Equal(TimeSpan.FromSeconds(180), TurnRetryPolicy.TotalBudget);
  }

  /// <summary>The first five waits double from a second; from there the cap holds. Six
  ///     attempts make five waits, so the loop never asks for the sixth one.</summary>
  [Fact]
  public void ComputeDelay_DoublesPerAttemptAtZeroJitter_AndTheCapHolds()
  {
    TimeSpan[] waits = [.. Enumerable.Range(1, 7).Select(attempt =>
        TurnRetryPolicy.ComputeDelay(attempt, 0.0))];

    Assert.Equal([
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
    ], waits);
  }

  [Fact]
  public void ComputeDelay_NeverExceedsTheCap()
  {
    for (int attempt = 1; attempt <= 20; attempt++)
    {
      Assert.True(TurnRetryPolicy.ComputeDelay(attempt, 1.0) <= TurnRetryPolicy.MaxDelay);
    }
  }

  [Fact]
  public void ComputeDelay_JitterMultipliesByOneToTwo()
  {
    Assert.Equal(TimeSpan.FromSeconds(2), TurnRetryPolicy.ComputeDelay(1, 1.0));
    Assert.Equal(TimeSpan.FromSeconds(1), TurnRetryPolicy.ComputeDelay(1, 0.0));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void ComputeDelay_RejectsAnAttemptBelowOne(int attempt) =>
      Assert.Throws<ArgumentOutOfRangeException>(() => TurnRetryPolicy.ComputeDelay(attempt, 0.0));

  [Theory]
  [InlineData(-0.5)]
  [InlineData(1.5)]
  public void ComputeDelay_RejectsJitterOutsideTheUnitInterval(double jitter) =>
      Assert.Throws<ArgumentOutOfRangeException>(() => TurnRetryPolicy.ComputeDelay(1, jitter));

  [Fact]
  public void AllowsRetry_BindsOnTheAttemptCap_NotTheTotalBudget()
  {
    // Five waits of 1, 2, 4, 8 and 16 s are 31 s nominal, well inside the 180 s budget, so
    // the attempt cap is what stops a sixth attempt.
    TimeSpan slept = TimeSpan.Zero;
    int attempt = 1;
    while (TurnRetryPolicy.AllowsRetry(attempt, slept, TurnRetryPolicy.ComputeDelay(attempt, 0.0)))
    {
      slept += TurnRetryPolicy.ComputeDelay(attempt, 0.0);
      attempt++;
    }

    Assert.Equal(6, attempt);
    Assert.Equal(TimeSpan.FromSeconds(31), slept);
  }

  [Fact]
  public void AllowsRetry_StopsWhenTheNextWaitWouldOverrunTheBudget()
  {
    Assert.False(TurnRetryPolicy.AllowsRetry(2, TimeSpan.FromSeconds(170), TimeSpan.FromSeconds(16)));
    Assert.True(TurnRetryPolicy.AllowsRetry(2, TimeSpan.FromSeconds(160), TimeSpan.FromSeconds(16)));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public void AllowsRetry_RejectsAnAttemptBelowOne(int attempt) =>
      Assert.Throws<ArgumentOutOfRangeException>(() =>
          TurnRetryPolicy.AllowsRetry(attempt, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
}
