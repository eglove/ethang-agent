namespace eThangAgent.AgentDomain.Tests;

/// <summary>A chain that cannot be completed and is not parked has no exit: <c>resume-parked</c>
///     refuses it, no session can complete a step it does not hold, and the status the domain
///     already treats as terminal was unreachable from every surface.
///     <para>These tests pin the exit, and the one thing it must refuse: abandoning is for a
///     chain whose work is being dropped, never a way to make a nearly-finished one look done.
///     </para></summary>
public class ChainAbandonTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 11, 1, 0, 0, TimeSpan.Zero);

  private static Chain Running(params int[] positions)
    => Chain.New("C:\\ws", 178, 1, "t", "root", positions.Length == 0 ? [1] : positions, T0);

  [Fact]
  public void Abandon_A_RunningChain_IsTerminal()
  {
    Chain abandoned = Running(1, 2).Abandon("the work is being dropped", T0);

    Assert.Equal(ChainStatus.Abandoned, abandoned.Status);
    Assert.Contains("the work is being dropped", abandoned.ParkReason, StringComparison.Ordinal);
  }

  [Fact]
  public void Abandon_AParkedChain_IsAllowed_ThatIsThePointOfAnExit()
  {
    Chain parked = Running(1).Park("waiting", 1, ChainActor.Harness, T0);

    Chain abandoned = parked.Abandon("dropped", T0);

    Assert.Equal(ChainStatus.Abandoned, abandoned.Status);
  }

  [Fact]
  public void Abandon_AFinishedChain_IsRefused_ItIsAlreadyTerminal()
  {
    Chain completed = Running(1) with { Status = ChainStatus.Completed };

    ChainInputException refused = Assert.Throws<ChainInputException>(
        () => completed.Abandon("dropped", T0));

    Assert.Contains("completed", refused.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Abandon_WithNoReason_IsRefused_AStatusNobodyCanExplainIsWorthless()
    => Assert.Throws<ArgumentException>(() => Running(1).Abandon("  ", T0));
}
