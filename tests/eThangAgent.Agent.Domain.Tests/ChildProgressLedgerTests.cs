namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #93: the progress ledger is the model-facing read surface for a
///     RUNNING child's mid-run activity. It keeps ONE latest snapshot per live child,
///     drops a child's entry when its settle event arrives (a terminal record has no
///     current phase), and never creates facts for children it did not observe.</summary>
public class ChildProgressLedgerTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public void Progress_Event_RecordsTheLatestSnapshot()
  {
    ChildProgressLedger ledger = new();
    AgentId child = AgentId.NewId();

    ledger.OnEvent(new ChildProgressEvent(child, T0, ChildPhase.ModelCall, "iteration"));
    ledger.OnEvent(new ChildProgressEvent(child, T0.AddSeconds(5), ChildPhase.ToolExec, "tool:read"));

    ChildProgressSnapshot? latest = ledger.Find(child);
    Assert.NotNull(latest);
    Assert.Equal(ChildPhase.ToolExec, latest.Phase);
    Assert.Equal("tool:read", latest.Label);
    Assert.Equal(T0.AddSeconds(5), latest.At);
  }

  [Fact]
  public void Settled_Event_DropsTheEntry()
  {
    ChildProgressLedger ledger = new();
    AgentId child = AgentId.NewId();
    ledger.OnEvent(new ChildProgressEvent(child, T0, ChildPhase.ToolExec, "tool:read"));

    ledger.OnEvent(new ChildSettledEvent(child, T0.AddSeconds(9), AgentStatus.Completed, null, 12));

    Assert.Null(ledger.Find(child));
  }

  [Fact]
  public void UnknownChild_HasNoSnapshot()
  {
    ChildProgressLedger ledger = new();

    Assert.Null(ledger.Find(AgentId.NewId()));
  }

  [Fact]
  public void OtherEventKinds_CreateNoFacts()
  {
    ChildProgressLedger ledger = new();
    AgentId child = AgentId.NewId();

    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "root-steer", 64));
    ledger.OnEvent(new ChildIdleAlertEvent(child, T0, TimeSpan.FromMinutes(3), "ModelCall"));

    Assert.Null(ledger.Find(child));
  }

  [Fact]
  public void Children_AreTrackedIndependently()
  {
    ChildProgressLedger ledger = new();
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();

    ledger.OnEvent(new ChildProgressEvent(first, T0, ChildPhase.ModelCall, "iteration"));
    ledger.OnEvent(new ChildProgressEvent(second, T0.AddSeconds(1), ChildPhase.Draining, "tool-result"));
    ledger.OnEvent(new ChildSettledEvent(first, T0.AddSeconds(2), AgentStatus.Completed, null, 1));

    Assert.Null(ledger.Find(first));
    Assert.Equal("tool-result", ledger.Find(second)!.Label);
  }
}
