namespace eThangAgent.AgentDomain.Tests;

/// <summary>A session that opens while its chain step is held by a PLACEHOLDER is the moment
///     the stranded claim is repaired: the session's own id is the fact the placeholder was
///     standing in for, so opening is when the correction can finally be written.
///     <para>These tests pin the decision, so the repair is a rule rather than a special case
///     buried in a factory.</para></summary>
public class ChainClaimAdoptionPolicyTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 11, 1, 0, 0, TimeSpan.Zero);

  private const string Session = "1ac54b6e-7562-49f2-9627-a34f7ee71945";

  private static Chain ChainWith(params (int Position, string Claimant)[] claims)
  {
    Chain chain = Chain.New("C:\\ws", 178, 1, "t", "root",
        [.. claims.Select(c => c.Position)], T0);
    foreach ((int position, string claimant) in claims)
    {
      chain = chain.ClaimStep(position, claimant, T0);
    }

    return chain;
  }

  [Fact]
  public void Decide_ForAStepHeldByAPlaceholder_AdoptsIt()
  {
    Chain chain = ChainWith((4, "pending-3d55fa35"));

    ChainClaimAdoption adoption = ChainClaimAdoptionPolicy.Decide(chain, 4, Session);

    Assert.Equal(ChainClaimAdoptionKind.Adopt, adoption.Kind);
    Assert.Null(adoption.Reason);
  }

  [Fact]
  public void Decide_ForAStepThisSessionAlreadyHolds_DoesNothing()
  {
    Chain chain = ChainWith((4, Session));

    ChainClaimAdoption adoption = ChainClaimAdoptionPolicy.Decide(chain, 4, Session);

    // Adoption runs at EVERY session open, so the already-correct case is the common one and
    // must not write, report, or refuse.
    Assert.Equal(ChainClaimAdoptionKind.Nothing, adoption.Kind);
  }

  [Fact]
  public void Decide_ForAStepHeldByAnotherRealSession_Refuses()
  {
    Chain chain = ChainWith((4, "someone-else"));

    ChainClaimAdoption adoption = ChainClaimAdoptionPolicy.Decide(chain, 4, Session);

    // A real claimant is the session doing the work. Adoption is not a way to take it.
    Assert.Equal(ChainClaimAdoptionKind.Refuse, adoption.Kind);
    Assert.Contains("someone-else", adoption.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Decide_ForAPositionTheChainDoesNotHave_Refuses()
  {
    Chain chain = ChainWith((1, Session));

    ChainClaimAdoption adoption = ChainClaimAdoptionPolicy.Decide(chain, 9, Session);

    Assert.Equal(ChainClaimAdoptionKind.Refuse, adoption.Kind);
    Assert.Contains("step 9", adoption.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void Decide_ForAChainThatIsFinished_DoesNothing()
  {
    Chain chain = ChainWith((1, "pending-abc")) with { Status = ChainStatus.Abandoned };

    ChainClaimAdoption adoption = ChainClaimAdoptionPolicy.Decide(chain, 1, Session);

    // A frozen chain takes no writes, and a repair is not an exception to that.
    Assert.Equal(ChainClaimAdoptionKind.Nothing, adoption.Kind);
  }
}
