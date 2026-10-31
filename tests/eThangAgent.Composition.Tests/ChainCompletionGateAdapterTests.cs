using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #304 D1 at the adapter: the CHAIN-side gate reads the SAME three keys and
///     gives the SAME answers as the plan-side one.
///     <para>WHY THIS IS A SEPARATE ADAPTER AND A SEPARATE TEST: the Plan Domain and the Agent
///     Domain may not reference each other, so each asks through its own port. What must not
///     differ is the ANSWER - one evaluator, one command set - and that is what these pin. A
///     chain that blocked where a plan did not would mean the floor depends on which domain
///     asked, which is the drift D1 exists to prevent.</para></summary>
public sealed class ChainCompletionGateAdapterTests : IDisposable
{
  private const string Ws = "C:\\ws\\chaingate";
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
      $"ethang-chaingate-{Guid.NewGuid():N}.db");

  public void Dispose()
  {
    GC.SuppressFinalize(this);
    // Named decision (CA1031): temp-db cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(_dbPath);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  private StateService State()
  {
    AppDatabase database = new(_dbPath);
    return new StateService(new SqliteStateStore(database), new NoEvidence(),
        FixedWorkspaceContext.ForPath(Ws), new InertChangeBus(), new NoGoalRunner(), null);
  }

  private static StateServiceChainCompletionGate Adapter(StateService state) =>
      new(new CompletionGate(new NoSnapshot()), state, () => Ws);

  // D2: an unset gate never blocks a chain's completion claim.
  [Fact]
  public async Task AWorkspaceNobodyAsked_NeverBlocks()
  {
    (bool allowed, string message) = await Adapter(State()).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(allowed);
    Assert.Contains("nobody has been asked", message, StringComparison.Ordinal);
  }

  // #285 D1: a decline is a DECISION, distinct from an omission - and neither blocks.
  [Fact]
  public async Task ADeclinedWorkspace_NeverBlocks_AndSaysItWasDeclined()
  {
    StateService state = State();
    _ = await state.SetAsync(CompletionGate.DeclinedKey, "{}", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    (bool allowed, string message) = await Adapter(state).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(allowed);
    Assert.Contains("DECLINED", message, StringComparison.Ordinal);
    Assert.DoesNotContain("nobody has been asked", message, StringComparison.Ordinal);
  }

  // THE ACCEPTANCE CRITERION, chain side: a declared gate blocks a completion claim that never
  // ran it.
  [Fact]
  public async Task ADeclaredGate_BlocksAClaimThatNeverRanIt()
  {
    StateService state = State();
    _ = await state.SetAsync(CompletionGate.GoalCheckKey, "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    (bool allowed, string message) = await Adapter(state).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(allowed);
    Assert.Contains("has never run", message, StringComparison.Ordinal);
  }

  // A DECLARED GATE WINS OVER A STALE DECLINE, so the answer never depends on read order.
  [Fact]
  public async Task ADeclaredGate_WinsOverAStaleDecline()
  {
    StateService state = State();
    _ = await state.SetAsync(CompletionGate.DeclinedKey, "{}", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await state.SetAsync(CompletionGate.GoalCheckKey, "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    (bool allowed, _) = await Adapter(state).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(allowed);
  }

  private sealed class NoEvidence : IEvidenceRunner
  {
    public Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, false, "not used here"));
  }

  private sealed class NoGoalRunner : IGoalCommandRunner
  {
    public Task<EvidenceResult> RunAsync(string workingDirectory, string command,
        CancellationToken ct = default)
        => Task.FromResult(new EvidenceResult(command, false, "not used here"));
  }

  private sealed class NoSnapshot : IWorkspaceSnapshotAccess
  {
    public Task<WorkspaceSnapshot> CaptureAsync(string workspaceRoot, CancellationToken ct = default)
        => Task.FromResult(WorkspaceSnapshot.Unavailable(workspaceRoot));
  }

  private sealed class InertChangeBus : IStateChangeBus
  {
    public IDisposable Subscribe(Action<StateChange> observer)
    {
      ArgumentNullException.ThrowIfNull(observer);
      return new Lease();
    }

    public void Publish(StateChange change)
    {
      // No subscribers on this path.
    }

    private sealed class Lease : IDisposable
    {
      public void Dispose()
      {
        // Nothing was subscribed.
      }
    }
  }
}
