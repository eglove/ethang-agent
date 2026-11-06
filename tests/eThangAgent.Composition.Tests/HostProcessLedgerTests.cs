namespace eThangAgent.Composition.Tests;

/// <summary>Issue #367: the supervisor's bounded exit wait used to discard what it learned,
///     and nothing recorded which session a live host belonged to. A host still in the
///     process table when the wait elapsed is a fact worth keeping - in production a host that
///     ignores a kill is a real defect, and in the suite it is what lets the leak guard name a
///     slow reap instead of reporting a presence.
///     <para>Process-wide and static by necessity: disposal runs on whichever thread closed
///     the session, and the caller that reads the facts (the assembly-level leak guard) is a
///     different object entirely. These tests pin the record's shape and its bound.</para>
///     <para>Deliberately NOT in the EnvironmentSensitive collection: nothing here touches a
///     real process, and the ledger is cleared around each case.</para></summary>
public sealed class HostProcessLedgerTests : IDisposable
{
  public HostProcessLedgerTests() => HostProcessLedger.Clear();

  public void Dispose()
  {
    HostProcessLedger.Clear();
    GC.SuppressFinalize(this);
  }

  [Fact]
  public void RecordSpawn_KeepsTheSessionWorkspaceAndPid_WithNoWait()
  {
    // The four facts are the whole point: a presence in the process table cannot say which
    // session owned the host, where it served, or whether anything ever asked it to exit.
    HostProcessLedger.Record(new HostProcessRecord(
        "ethang-conv-ABC", @"C:\work", 4242, Waited: null));

    HostProcessRecord record = Assert.Single(HostProcessLedger.Snapshot());

    Assert.Equal("ethang-conv-ABC", record.SessionId);
    Assert.Equal(@"C:\work", record.Workspace);
    Assert.Equal(4242, record.ProcessId);
    Assert.Null(record.Waited);
  }

  [Fact]
  public void RecordWaitElapsed_KeepsHowLongTheSupervisorWaited()
  {
    HostProcessLedger.Record(new HostProcessRecord(
        "ethang-conv-ABC", @"C:\work", 4242, TimeSpan.FromSeconds(10)));

    HostProcessRecord record = Assert.Single(HostProcessLedger.Snapshot());

    Assert.Equal(TimeSpan.FromSeconds(10), record.Waited);
  }

  [Fact]
  public void Snapshot_IsEmpty_WhenNoHostWasStarted() =>
      Assert.Empty(HostProcessLedger.Snapshot());

  [Fact]
  public void Snapshot_IsACopy_SoAReaderCannotMutateTheLedger()
  {
    HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null));

    IReadOnlyList<HostProcessRecord> first = HostProcessLedger.Snapshot();
    HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 2, Waited: null));

    _ = Assert.Single(first);
    Assert.Equal(2, HostProcessLedger.Snapshot().Count);
  }

  [Fact]
  public void Record_IsBounded_KeepingTheMostRecent()
  {
    // Bounded on purpose: the ledger exists to explain a presence, not to archive history,
    // so a long run cannot grow it without limit.
    for (int pid = 1; pid <= 300; pid++)
    {
      HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", pid, Waited: null));
    }

    IReadOnlyList<HostProcessRecord> facts = HostProcessLedger.Snapshot();

    Assert.Equal(256, facts.Count);
    Assert.Equal(300, facts[^1].ProcessId);
    Assert.Equal(45, facts[0].ProcessId);
  }

  [Fact]
  public void Clear_DropsEveryFact()
  {
    HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null));

    HostProcessLedger.Clear();

    Assert.Empty(HostProcessLedger.Snapshot());
  }
}
