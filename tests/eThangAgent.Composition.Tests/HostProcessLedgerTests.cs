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
  public void Record_StampsTheAmbientTestOwner_WhenTheFactNamesNone()
  {
    // The call site cannot know the test: a session's container is built on a thread whose
    // execution context was suppressed, so the test framework's own ambient context is null
    // there. The owner therefore travels as ambient state, and the record picks it up.
    using (HostProcessLedger.OwnedBy("Suite.SomeTest"))
    {
      HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null));
    }

    Assert.Equal("Suite.SomeTest", Assert.Single(HostProcessLedger.Snapshot()).TestOwner);
  }

  [Fact]
  public void Record_KeepsTheOwnersName_WhenTheFactNamesOne()
  {
    // An explicit owner wins: a caller that knows whose host it is must not be overwritten by
    // whatever scope happens to be ambient.
    using (HostProcessLedger.OwnedBy("Suite.Ambient"))
    {
      HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null,
          TestOwner: "Suite.Explicit"));
    }

    Assert.Equal("Suite.Explicit", Assert.Single(HostProcessLedger.Snapshot()).TestOwner);
  }

  [Fact]
  public void Record_LeavesTheOwnerNull_OutsideATestScope()
  {
    HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null));

    Assert.Null(Assert.Single(HostProcessLedger.Snapshot()).TestOwner);
  }

  [Fact]
  public void OwnedBy_RestoresThePreviousOwner_WhenTheScopeEnds()
  {
    using (HostProcessLedger.OwnedBy("Suite.Outer"))
    {
      using (HostProcessLedger.OwnedBy("Suite.Inner"))
      {
        Assert.Equal("Suite.Inner", HostProcessLedger.TestOwner);
      }

      Assert.Equal("Suite.Outer", HostProcessLedger.TestOwner);
    }

    Assert.Null(HostProcessLedger.TestOwner);
  }

  [Fact]
  public async Task Record_StampsTheOwner_CarriedIntoASuppressedFlow()
  {
    // THE CUT IS THE WHOLE PROBLEM: a session's container is built on a thread whose execution
    // context was suppressed, and suppression drops an AsyncLocal as well as the framework's
    // context - so a claim left ambient reads null inside the build and every fact loses the
    // test that caused it. Captured before the cut and restored inside it, the owner survives.
    using (HostProcessLedger.OwnedBy("Suite.SomeTest"))
    {
      OwnerScope? carried = HostProcessLedger.CaptureOwner();
      Task<string?> scheduled;
      using (ExecutionContext.SuppressFlow())
      {
        scheduled = Task.Run(() =>
        {
          HostProcessLedger.RestoreOwner(carried);
          HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null));
          return HostProcessLedger.TestOwner;
        });
      }

      Assert.Equal("Suite.SomeTest", await scheduled.ConfigureAwait(true));
    }

    Assert.Equal("Suite.SomeTest", Assert.Single(HostProcessLedger.Snapshot()).TestOwner);
  }

  [Fact]
  public void Clear_DropsEveryFact()
  {
    HostProcessLedger.Record(new HostProcessRecord("s", @"C:\w", 1, Waited: null));

    HostProcessLedger.Clear();

    Assert.Empty(HostProcessLedger.Snapshot());
  }
}
