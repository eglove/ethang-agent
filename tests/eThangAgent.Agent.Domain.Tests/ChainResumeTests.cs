namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #303's missing exit: resuming a PARKED chain has to un-park it AND re-arm the
///     step that stopped it, or the chain stays stuck wearing a Running status.
///     <para>THE DEFECT THIS PINS. <see cref="Chain.Resume"/> moved Parked to Running and nothing
///     else. The step the park named is almost always Failed or Parked itself - it is what
///     stopped the chain - so the cursor found nothing available, the chain blocked, and it
///     parked again on the next completion. A user's resume appeared to do nothing.</para></summary>
public class ChainResumeTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

  private static Chain NewChain(params int[] positions)
      => Chain.New("C:\\ws\\alpha", 7, 3, "Ship it", "session-a", positions, T0);

  [Fact]
  public void ResumeAndRearm_ReturnsTheFailedStepToPending_WithAFreshAttemptBudget()
  {
    Chain chain = NewChain(1, 2)
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Failed, ChainActor.Session("session-a"), "it broke", T0)
        .Park("step 1 failed 3 times", 1, ChainActor.Harness, T0);

    Chain resumed = chain.ResumeAndRearm(ChainActor.User, T0);

    Assert.Equal(ChainStatus.Running, resumed.Status);
    Assert.Null(resumed.ParkReason);
    Assert.Null(resumed.ParkedAtPosition);
    ChainStep step = resumed.StepAt(1)!;
    Assert.Equal(ChainStepStatus.Pending, step.Status);
    Assert.Equal(0, step.Attempts);
    Assert.Null(step.SessionId);
    Assert.Null(step.Outcome);
    Assert.True(step.IsAvailable);
  }

  [Fact]
  public void ResumeAndRearm_LeavesAFinishedStepAlone()
  {
    // A park names the step that STOPPED the chain, so re-arming finished work would make a
    // resume redo it.
    Chain chain = NewChain(1, 2)
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .Park("nothing to start", 1, ChainActor.Harness, T0);

    Chain resumed = chain.ResumeAndRearm(ChainActor.User, T0);

    Assert.Equal(ChainStepStatus.Done, resumed.StepAt(1)!.Status);
    Assert.Equal(ChainStatus.Running, resumed.Status);
  }

  [Fact]
  public void ResumeAndRearm_ClearsInvalidationMarkers()
  {
    // Invalidation is the other way a chain becomes uncompletable, and a resume is the same
    // human decision about both: 'this work should proceed'.
    Chain chain = NewChain(1, 2)
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .InvalidateStep(1, "refutation-7", T0)
        .Park("invalidated", 1, ChainActor.Harness, T0);

    Chain resumed = chain.ResumeAndRearm(ChainActor.User, T0);

    Assert.Empty(resumed.InvalidatedSteps);
  }

  [Fact]
  public void ResumeAndRearm_WithNoParkedPosition_StillResumes()
  {
    Chain chain = NewChain(1).Park("nothing was available", null, ChainActor.Harness, T0);

    Chain resumed = chain.ResumeAndRearm(ChainActor.User, T0);

    Assert.Equal(ChainStatus.Running, resumed.Status);
    Assert.Null(resumed.ParkReason);
  }

  [Fact]
  public void ResumeAndRearm_OnACompletedChain_Throws()
  {
    Chain chain = NewChain(1)
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Done, ChainActor.Session("session-a"), null, T0)
        .SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);

    _ = Assert.Throws<ChainInputException>(() => chain.ResumeAndRearm(ChainActor.User, T0));
  }
}
