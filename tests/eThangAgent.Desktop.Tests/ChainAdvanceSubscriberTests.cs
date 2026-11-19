using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>Which plan facts are allowed to advance a chain. A chain advances on a
///     COMPLETION, and of the step changes only an UPDATE can carry one: a step added,
///     moved, or removed is a STRUCTURE edit, which is the very thing the structure lock
///     exists to bound.
///     <para>THE DEFECT THESE PIN: every 'steps' fact was read as a completion, so a chain
///     step that appended to its own plan ended its own chain. The append arrived as a
///     completion, settled the step, and the chain completed before the session could mark
///     anything done - the hinge's own Done then landed on a chain that had already
///     finished, and the appended step was never the chain's to execute. It reproduced only
///     under load, because the append and the completion had to interleave with the advance
///     to be visible at all.</para>
///     <para>THE ASSERTION IS 'THE ADVANCE IS NEVER ASKED FOR'. The rule is a guard, so a
///     fact that must not advance never reaches the resolve - which is stronger than
///     asserting the advance declined, because a resolved advance has already read the chain
///     record and could still have written to it.</para></summary>
public class ChainAdvanceSubscriberTests
{
  /// <summary>An append is a structure edit, not a completion.</summary>
  [Fact]
  public async Task AStepAdded_IsNotACompletion()
  {
    Probe probe = new();

    await Subscriber(probe).OnEventAsync(Fact("steps", "added"), "ws",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(0, probe.Resolved);
  }

  /// <summary>Nor is a move or a removal - both renumber the positions a chain binds to.</summary>
  [Theory]
  [InlineData("moved")]
  [InlineData("removed")]
  public async Task AStructureEdit_IsNotACompletion(string kind)
  {
    Probe probe = new();

    await Subscriber(probe).OnEventAsync(Fact("steps", kind), "ws",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(0, probe.Resolved);
  }

  /// <summary>An update IS the completion fact, and reaches the advance.</summary>
  [Fact]
  public async Task AStepUpdated_IsACompletion()
  {
    Probe probe = new();

    await Subscriber(probe).OnEventAsync(Fact("steps", "updated"), "ws",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(1, probe.Resolved);
  }

  /// <summary>A creation or a status move is not a step at all.</summary>
  [Theory]
  [InlineData("created")]
  [InlineData("status")]
  public async Task ANonStepChange_IsNotACompletion(string change)
  {
    Probe probe = new();

    await Subscriber(probe).OnEventAsync(Fact(change, null), "ws",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(0, probe.Resolved);
  }

  /// <summary>A subscriber over a probe, reporting nothing.</summary>
  private static ChainAdvanceSubscriber Subscriber(Probe probe) =>
      new(_ => probe.Resolve(), _ => { });

  /// <summary>One plan-change fact naming a change kind and, for a step change, what the
  ///     step change was.</summary>
  private static PlanChangedEvent Fact(string change, string? stepChange) =>
      new(AgentId.NewId(), DateTimeOffset.UtcNow, 1, change, "Abandoned", stepChange);

  /// <summary>Counts how often the subscriber asked for an advance. No advance is ever
  ///     constructed: the rule under test is the guard in front of it.</summary>
  private sealed class Probe
  {
    public int Resolved { get; private set; }

    public ChainAdvanceService? Resolve()
    {
      Resolved++;
      return null;
    }
  }
}
