using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #285: the plan-side gate adapter reads the DECLINE as its own fact, so a
///     workspace whose user declined is distinguishable from one nobody ever asked - and NEITHER
///     blocks a completion claim.
///     <para>THE ASYMMETRY THIS PINS: a declared gate must still block an ungated claim (that is
///     #283's whole point), while a declined or unasked one must never block. A fix that made
///     every absent gate permissive would silently switch the feature off.</para></summary>
public sealed class DeclinedGateAdapterTests : IDisposable
{
  private const string Ws = "C:\\ws\\declined";
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
      $"ethang-declined-{Guid.NewGuid():N}.db");

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

  private static StateServicePlanCompletionGate Adapter(StateService state) =>
      new(new CompletionGate(new NoSnapshot()), state, () => Ws);

  [Fact]
  public async Task AWorkspaceNobodyAsked_NeverBlocks()
  {
    (bool allowed, string message) = await Adapter(State()).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(allowed);
    Assert.Contains("nobody has been asked", message, StringComparison.Ordinal);
  }

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
    // The two absences must not render alike: a user auditing a workspace needs to know a
    // decision was made rather than that nobody asked.
    Assert.DoesNotContain("nobody has been asked", message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ADeclaredGate_StillBlocksAClaimThatNeverRanIt()
  {
    // The other direction, so the permissive fix cannot swallow #283.
    StateService state = State();
    _ = await state.SetAsync(CompletionGate.GoalCheckKey, "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    (bool allowed, string message) = await Adapter(state).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(allowed);
    Assert.Contains("has never run", message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ADeclaredGate_WinsOverAStaleDecline()
  {
    // A user who declined and later declared must be gated by what they declared. Leaving the
    // decline readable would make the answer depend on read order.
    StateService state = State();
    _ = await state.SetAsync(CompletionGate.DeclinedKey, "{}", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    _ = await state.SetAsync(CompletionGate.GoalCheckKey, "[\"dotnet build\"]", null,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    (bool allowed, string message) = await Adapter(state).EvaluateAsync(
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(allowed);
    Assert.Contains("has never run", message, StringComparison.Ordinal);
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
