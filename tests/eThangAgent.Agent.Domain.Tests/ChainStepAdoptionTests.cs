namespace eThangAgent.AgentDomain.Tests;

/// <summary>A chain step claimed under a PLACEHOLDER id is a guess, not an owner: the harness
///     claims before the session's id exists, and correcting that claim must be possible.
///     <para>These tests exist because the correction was the one write in the advance whose
///     failure nobody read, and a step stranded on a placeholder cannot be completed by the
///     session actually doing the work.</para></summary>
public class ChainStepAdoptionTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 11, 1, 0, 0, TimeSpan.Zero);

  private static Chain ChainWithClaim(string? claimant)
  {
    Chain chain = Chain.New("C:\\ws", 178, 1, "t", "root", [1], T0);
    return claimant is null
      ? chain
      : chain.ClaimStep(1, claimant, T0);
  }

  [Fact]
  public void AdoptStep_OnAPlaceholderClaim_RecordsTheRealSessionAndKeepsTheAttempt()
  {
    Chain adopted = ChainWithClaim("pending-3d55fa354ac7454fa5a5b9360ceb62e1")
        .AdoptStep(1, "1ac54b6e-7562-49f2-9627-a34f7ee71945", T0);

    ChainStep step = adopted.Steps[0];
    Assert.Equal("1ac54b6e-7562-49f2-9627-a34f7ee71945", step.SessionId);
    Assert.Equal(1, step.Attempts);
    Assert.Equal(ChainStepStatus.Running, step.Status);
    Assert.Equal("session:1ac54b6e-7562-49f2-9627-a34f7ee71945", step.Actor.Encoded);
  }

  [Fact]
  public void AdoptStep_OnAPendingStep_IsRefused_ThereIsNoClaimToCorrect()
  {
    Chain pending = ChainWithClaim(null);

    ChainInputException refused = Assert.Throws<ChainInputException>(
        () => pending.AdoptStep(1, "real-session", T0));

    Assert.Contains("Pending", refused.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void AdoptStep_OnAnotherSessionsRealClaim_IsRefused()
  {
    ChainInputException refused = Assert.Throws<ChainInputException>(
        () => ChainWithClaim("session-a").AdoptStep(1, "session-b", T0));

    Assert.Contains("session-a", refused.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void AdoptStep_ForTheSessionThatAlreadyHoldsIt_ChangesNothing()
  {
    Chain chain = ChainWithClaim("session-a");

    Chain adopted = chain.AdoptStep(1, "session-a", T0);

    Assert.Equal(chain.Steps[0], adopted.Steps[0]);
  }

  [Fact]
  public void IsPlaceholderClaimant_IsTrueOnlyForTheProvisionalSpelling()
  {
    Assert.True(ChainStep.IsPlaceholderClaimant("pending-abc"));
    Assert.False(ChainStep.IsPlaceholderClaimant("1ac54b6e-7562-49f2-9627-a34f7ee71945"));
    Assert.False(ChainStep.IsPlaceholderClaimant(null));
  }
}
