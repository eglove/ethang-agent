using eThangAgent.AgentDomain;

namespace eThangAgent.Agent.Domain.Tests;

/// <summary>The stuck-chain rule: what a chain needs when nothing will move it. PURE, so this
///     pins the answers with no store, no clock, and no session.
///     <para>THE STATE THIS EXISTS FOR is a Running chain whose cursor has nothing to start and
///     no session holding anything - the shape that sat wedged until the only exit was abandon.
///     </para></summary>
public class ChainStuckPolicyTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

  private static Chain NewChain(params ChainStepSeed[] seeds) =>
      Chain.NewFromSeeds("C:\\ws", 7, 1, "Ship it", "session-root", seeds, T0);

  private static ChainStepSeed Implement(int position) => ChainStepSeed.Implementing(position);

  private static Chain Done(Chain chain, int position) =>
      chain.ClaimStep(position, "session-" + position, T0)
          .SettleStep(position, ChainStepStatus.Done, ChainActor.Session("session-" + position),
              null, T0);

  [Fact]
  public void RepairFor_StartsTheStepTheCursorOffers_WhenNothingIsRunning()
  {
    // A chain whose step 1 settled but whose advance never started step 2: the cursor has work
    // available and no session holds it, so the repair is to start it.
    Chain chain = Done(NewChain(Implement(1), Implement(2)), 1);

    ChainRepair repair = ChainStuckPolicy.RepairFor(chain)!;

    Assert.Equal(ChainRepairKind.StartStep, repair.Kind);
    Assert.Equal(2, repair.Position);
  }

  [Fact]
  public void RepairFor_IsNull_WhileAStepIsRunning()
  {
    // A LIVE SESSION HOLDS THE WORK. Its completion is what advances the chain, and starting a
    // second session would duplicate it.
    Chain chain = NewChain(Implement(1), Implement(2)).ClaimStep(1, "session-a", T0);

    Assert.Null(ChainStuckPolicy.RepairFor(chain));
  }

  [Fact]
  public void RepairFor_CompletesAChainWhoseEveryStepSettled()
  {
    // The work finished and the last write did not land: the record is what needs correcting.
    Chain chain = Done(Done(NewChain(Implement(1), Implement(2)), 1), 2);

    ChainRepair repair = ChainStuckPolicy.RepairFor(chain)!;

    Assert.Equal(ChainRepairKind.CompleteChain, repair.Kind);
    Assert.Null(repair.Position);
  }

  [Fact]
  public void RepairFor_ParksAChainBlockedOnAFailedStep_NamingIt()
  {
    // A FAILED STEP WILL NEVER SETTLE BY ITSELF, so there is nothing to start and nothing that
    // can complete. Parking converts a state nothing can act on into one a human can.
    Chain chain = NewChain(Implement(1), Implement(2))
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Failed, ChainActor.Session("session-a"), "it broke", T0);

    ChainRepair repair = ChainStuckPolicy.RepairFor(chain)!;

    Assert.Equal(ChainRepairKind.ParkChain, repair.Kind);
    Assert.Equal(1, repair.Position);
    Assert.Contains("1 (Failed)", repair.Reason, StringComparison.Ordinal);
    // THE REASON NAMES THE EXITS: a park with no way out is a dead end with a label.
    Assert.Contains("Resume", repair.Reason, StringComparison.Ordinal);
    Assert.Contains("abandon", repair.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void RepairFor_NamesEveryBlockedStep_NotOnlyTheFirst()
  {
    // A chain blocked on two steps needs both fixed; naming one would make the next repair
    // report the other as though it were new.
    Chain chain = NewChain(Implement(1), Implement(2))
        .ClaimStep(1, "session-a", T0)
        .SettleStep(1, ChainStepStatus.Failed, ChainActor.Session("session-a"), null, T0)
        .ClaimStep(2, "session-b", T0)
        .SettleStep(2, ChainStepStatus.Parked, ChainActor.Session("session-b"), null, T0);

    ChainRepair repair = ChainStuckPolicy.RepairFor(chain)!;

    Assert.Contains("1 (Failed)", repair.Reason, StringComparison.Ordinal);
    Assert.Contains("2 (Parked)", repair.Reason, StringComparison.Ordinal);
  }

  [Fact]
  public void RepairFor_IsNull_ForAParkedChain()
  {
    // PARKED IS WAITING FOR A HUMAN ON PURPOSE. Repairing one would undo a decision somebody
    // made, and resume-parked is the action that exists for it.
    Chain parked = NewChain(Implement(1), Implement(2))
        .Park("a human is looking", 1, ChainActor.Harness, T0);

    Assert.Null(ChainStuckPolicy.RepairFor(parked));
  }

  [Fact]
  public void RepairFor_IsNull_ForAFinishedChain()
  {
    Chain chain = NewChain(Implement(1), Implement(2));
    Chain completed = Done(Done(chain, 1), 2)
        .SetStatus(ChainStatus.Completed, ChainActor.Harness, T0);

    Assert.Null(ChainStuckPolicy.RepairFor(completed));
  }

  [Fact]
  public void RepairFor_IsNull_ForAnAbandonedChain()
  {
    Chain abandoned = NewChain(Implement(1), Implement(2)).Abandon("dropped", T0);

    Assert.Null(ChainStuckPolicy.RepairFor(abandoned));
  }
}
