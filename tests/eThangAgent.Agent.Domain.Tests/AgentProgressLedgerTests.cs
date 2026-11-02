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
    AgentProgressLedger ledger = new();
    AgentId child = AgentId.NewId();

    ledger.OnEvent(new AgentProgressEvent(child, T0, AgentPhase.ModelCall, "iteration"));
    ledger.OnEvent(new AgentProgressEvent(child, T0.AddSeconds(5), AgentPhase.ToolExec, "tool:read"));

    AgentProgressSnapshot? latest = ledger.Find(child);
    Assert.NotNull(latest);
    Assert.Equal(AgentPhase.ToolExec, latest.Phase);
    Assert.Equal("tool:read", latest.Label);
    Assert.Equal(T0.AddSeconds(5), latest.At);
  }

  [Fact]
  public void Settled_Event_DropsTheEntry()
  {
    AgentProgressLedger ledger = new();
    AgentId child = AgentId.NewId();
    ledger.OnEvent(new AgentProgressEvent(child, T0, AgentPhase.ToolExec, "tool:read"));

    ledger.OnEvent(new AgentSettledEvent(child, T0.AddSeconds(9), AgentStatus.Completed, null, 12));

    Assert.Null(ledger.Find(child));
  }

  [Fact]
  public void UnknownChild_HasNoSnapshot()
  {
    AgentProgressLedger ledger = new();

    Assert.Null(ledger.Find(AgentId.NewId()));
  }

  [Fact]
  public void OtherEventKinds_CreateNoFacts()
  {
    AgentProgressLedger ledger = new();
    AgentId child = AgentId.NewId();

    ledger.OnEvent(new MessageDeliveredEvent(child, T0, "root-steer", 64));
    ledger.OnEvent(new AgentIdleAlertEvent(child, T0, TimeSpan.FromMinutes(3), "ModelCall"));

    Assert.Null(ledger.Find(child));
  }

  [Fact]
  public void Children_AreTrackedIndependently()
  {
    AgentProgressLedger ledger = new();
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();

    ledger.OnEvent(new AgentProgressEvent(first, T0, AgentPhase.ModelCall, "iteration"));
    ledger.OnEvent(new AgentProgressEvent(second, T0.AddSeconds(1), AgentPhase.Draining, "tool-result"));
    ledger.OnEvent(new AgentSettledEvent(first, T0.AddSeconds(2), AgentStatus.Completed, null, 1));

    Assert.Null(ledger.Find(first));
    Assert.Equal("tool-result", ledger.Find(second)!.Label);
  }
}
