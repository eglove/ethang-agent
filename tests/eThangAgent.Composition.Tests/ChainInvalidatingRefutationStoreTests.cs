using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;
using eThangAgent.StateDomain;
using eThangAgent.Storage.ACL;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #304 D5: a refutation MARKS the step whose conclusion it disproved.
///     <para>WHY A DECORATOR ON THE REFUTATION PORT: D5 says the marker is set BY THE
///     FALSIFICATION - not by a human and not by an agent - so the write has to happen where a
///     refutation is written. The experiment tool's refuted-verdict path is that one site, so
///     decorating it is what makes the rule structural rather than a second call site somebody
///     can forget.</para>
///     <para>WHICH STEP IS STATED, NOT GUESSED: the nearest PRECEDING finished step of the
///     session that recorded the finding - the <c>Chain.FixTargetFor</c> rule #320 already
///     uses. The ledger is a flat append-only list with no per-record step attribution, so the
///     harness can name the work a refutation came AFTER but not the exact step that concluded
///     the disproved thing; a per-record attribution would be a second record of what the chain
///     already says.</para></summary>
public sealed class ChainInvalidatingRefutationStoreTests : IDisposable
{
  private const string Ws = "C:\\ws\\refute";
  private static readonly DateTimeOffset T0 = new(2026, 10, 30, 13, 0, 0, TimeSpan.Zero);
  private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
      $"ethang-refute-{Guid.NewGuid():N}.db");

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

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  /// <summary>A two-step chain whose step 1 is Done (claimed by <c>session-a</c>) and whose
  ///     step 2 is Running for <c>session-b</c> - the shape a refutation mid-chain produces.
  ///     </summary>
  private async Task<(SqliteChainStore Store, ChainService Chains, Chain Chain)> ChainAsync()
  {
    SqliteChainStore store = new(new AppDatabase(_dbPath), Ws);
    ChainService chains = new(store);
    Chain created = Must(await chains.CreateAsync(
        ChainCreation.Implementing(Ws, 12, 4, "t", "session-root", [1, 2]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(created.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.SettleStepAsync(created.Id, 1, ChainStepStatus.Done,
        ChainActor.Session("session-a"), "ok", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    Chain running = Must(await chains.ClaimStepAsync(created.Id, 2, "session-b", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    return (store, chains, running);
  }

  private static ChainInvalidatingRefutationStore Decorator(ChainService? chains, string? session,
      string workspace) =>
      new(new StateServiceRefutationStore(new StateService(
          new SqliteStateStore(new AppDatabase(workspace + "-state.db")), new NoEvidence(),
          FixedWorkspaceContext.ForPath(workspace), new InertChangeBus(), new NoGoalRunner(), null)),
          () => chains, () => session, TimeProvider.System);

  // THE ACCEPTANCE CRITERION: a refutation marks the earlier step it came after.
  [Fact]
  public async Task Write_MarksThePrecedingDoneStep()
  {
    (SqliteChainStore store, ChainService chains, Chain chain) = await ChainAsync();

    _ = await Decorator(chains, "session-b", Ws).WriteAsync("refutations/slug-p1", "refutation ...",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Chain loaded = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal("refutations/slug-p1", loaded.Steps[0].InvalidatedBy);
    Assert.Equal(ChainStepStatus.Done, loaded.Steps[0].Status);
  }

  // AND THE CHAIN THEN CANNOT COMPLETE: the marker is not decoration.
  [Fact]
  public async Task Write_MakesTheChainUncompletable()
  {
    (_, ChainService chains, Chain chain) = await ChainAsync();

    _ = await Decorator(chains, "session-b", Ws).WriteAsync("refutations/slug-p1", "refutation ...",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Chain loaded = Must(await chains.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    _ = Assert.Throws<ChainInputException>(
        () => loaded.SetStatus(ChainStatus.Completed, ChainActor.Harness, T0));
  }

  // A REFUTATION WITH NOTHING BEFORE IT invalidates nothing: there is no earlier conclusion to
  // disprove, and the finding is still shared.
  [Fact]
  public async Task Write_FromTheChainsFirstStep_InvalidatesNothing()
  {
    SqliteChainStore store = new(new AppDatabase(_dbPath), Ws);
    ChainService chains = new(store);
    Chain created = Must(await chains.CreateAsync(
        ChainCreation.Implementing(Ws, 12, 4, "t", "session-root", [1]), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(created.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Result<int> written = await Decorator(chains, "session-a", Ws)
        .WriteAsync("refutations/slug-p1", "refutation ...",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(written.IsSuccess);
    Chain loaded = Must(await store.GetAsync(created.Id, TestContext.Current.CancellationToken));
    Assert.False(loaded.Steps[0].IsInvalidated);
  }

  // AN ORDINARY SESSION writes its refutation and invalidates nothing: the shared-finding half
  // of #315 is for every session, chain or not.
  [Fact]
  public async Task Write_FromASessionInNoChain_StillWrites()
  {
    (SqliteChainStore store, ChainService chains, Chain chain) = await ChainAsync();

    Result<int> written = await Decorator(chains, "session-stranger", Ws)
        .WriteAsync("refutations/slug-p1", "refutation ...",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(written.IsSuccess);
    Chain loaded = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.All(loaded.Steps, step => Assert.False(step.IsInvalidated));
  }

  // A CONTAINER WITH NO CHAIN MACHINERY writes the refutation and invalidates nothing - the
  // pre-#304 behaviour, byte for byte.
  [Fact]
  public async Task Write_WithNoChainMachinery_StillWrites()
  {
    Result<int> written = await Decorator(chains: null, session: "session-a", Ws)
        .WriteAsync("refutations/slug-p1", "refutation ...",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(written.IsSuccess);
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
