using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #304 D5: a later refutation marks the earlier step INVALIDATED without
///     changing its status, and the chain cannot complete while any step is invalidated.
///     <para>THE TWO FACTS BOTH SURVIVE, which is the whole design: the step was DONE, and its
///     conclusion no longer holds. Rewriting the status would be a lie about what happened;
///     letting the chain complete would be a lie about what is true.</para></summary>
public class ChainInvalidationTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 11, 0, 0, TimeSpan.Zero);

  /// <summary>A one-step chain whose only step is Done and claimed by <c>session-a</c>.
  ///     <para>Built by mutating the aggregate rather than by hand, so the step's own invariants
  ///     (a claim is an attempt, settling clears the clock) hold exactly as they do in
  ///     production.</para></summary>
  private static Chain Done()
  {
    Chain chain = Chain.NewFromSeeds("C:\\ws", 3, 1, "t", "session-root",
        [new ChainStepSeed(1, ChainStepRole.Implement)], T0);
    return chain
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), "finished", T0);
  }

  // A step is INVALIDATED without its status changing - the two facts are kept apart.
  [Fact]
  public void InvalidateStep_KeepsTheStatusAndRecordsTheRefutation()
  {
    Chain chain = Done().InvalidateStep(1, "refutations/slug-p1", T0);

    ChainStep step = chain.Steps[0];
    Assert.Equal(ChainStepStatus.Done, step.Status);
    Assert.True(step.IsInvalidated);
    Assert.Equal("refutations/slug-p1", step.InvalidatedBy);
  }

  // FIRST WINS: the refusal names ONE refutation, and the earlier disproof is the one that
  // made the claim untrue.
  [Fact]
  public void InvalidateStep_Twice_KeepsTheFirstRefutation()
  {
    Chain chain = Done()
        .InvalidateStep(1, "refutations/first", T0)
        .InvalidateStep(1, "refutations/second", T0);

    Assert.Equal("refutations/first", chain.Steps[0].InvalidatedBy);
  }

  // THE COMPLETION REFUSAL: the whole point of the marker.
  [Fact]
  public void Completed_WithAnInvalidatedStep_IsRefused()
  {
    Chain chain = Done().InvalidateStep(1, "refutations/slug-p1", T0);

    ChainInputException refused = Assert.Throws<ChainInputException>(
        () => chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0));

    Assert.Contains("invalidated", refused.Message, StringComparison.Ordinal);
    Assert.Contains("refutations/slug-p1", refused.Message, StringComparison.Ordinal);
  }

  // D3: the refusal names BOTH exits - the human's clear, and abandoning the chain.
  [Fact]
  public void Completed_WithAnInvalidatedStep_NamesBothExits()
  {
    Chain chain = Done().InvalidateStep(1, "refutations/slug-p1", T0);

    ChainInputException refused = Assert.Throws<ChainInputException>(
        () => chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0));

    Assert.Contains("HUMAN", refused.Message, StringComparison.Ordinal);
    Assert.Contains("Abandoned", refused.Message, StringComparison.Ordinal);
  }

  // A CLEAN CHAIN IS UNTOUCHED: the invalidation invariant must not block ordinary completion.
  [Fact]
  public void Completed_WithNoInvalidatedStep_IsAllowed()
  {
    Chain chain = Done().SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);

    Assert.Equal(ChainStatus.Completed, chain.Status);
  }

  // THE HUMAN'S EXIT: clearing the markers is what lets the chain complete again.
  [Fact]
  public void ClearInvalidation_LetsTheChainComplete()
  {
    Chain chain = Done()
        .InvalidateStep(1, "refutations/slug-p1", T0)
        .ClearInvalidation(T0)
        .SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);

    Assert.Equal(ChainStatus.Completed, chain.Status);
    Assert.False(chain.Steps[0].IsInvalidated);
  }

  // ABANDONED IS NEVER GATED: dropping a half-finished chain stays the honest exit, even with
  // an invalidated step.
  [Fact]
  public void Abandoned_WithAnInvalidatedStep_IsStillAllowed()
  {
    Chain chain = Done().InvalidateStep(1, "refutations/slug-p1", T0)
        .SetStatus(ChainStatus.Abandoned, ChainActor.User, T0);

    Assert.Equal(ChainStatus.Abandoned, chain.Status);
  }

  // AN UNSETTLED CHAIN IS TOLD ABOUT THE UNSETTLED STEP FIRST: that is the actionable one.
  [Fact]
  public void Completed_WithBothProblems_ReportsTheUnsettledStep()
  {
    Chain chain = Chain.NewFromSeeds("C:\\ws", 3, 1, "t", "session-root",
        [new ChainStepSeed(1, ChainStepRole.Implement),
         new ChainStepSeed(2, ChainStepRole.Implement)], T0) with
    {
      Id = 1,
      Version = 1,
    };
    chain = chain.InvalidateStep(1, "refutations/slug-p1", T0);

    ChainInputException refused = Assert.Throws<ChainInputException>(
        () => chain.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0));

    Assert.Contains("unsettled", refused.Message, StringComparison.Ordinal);
  }

  // The chain's own status is untouched by an invalidation: the remaining steps can still run.
  [Fact]
  public void InvalidateStep_DoesNotParkOrCompleteTheChain()
  {
    Chain chain = Done().InvalidateStep(1, "refutations/slug-p1", T0);

    Assert.Equal(ChainStatus.Running, chain.Status);
  }

  // A TERMINAL chain is frozen, invalidation included: the record has already been read.
  [Fact]
  public void InvalidateStep_OnACompletedChain_IsRefused()
  {
    Chain completed = Done().SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);

    _ = Assert.Throws<ChainInputException>(
        () => completed.InvalidateStep(1, "refutations/slug-p1", T0));
  }
}
