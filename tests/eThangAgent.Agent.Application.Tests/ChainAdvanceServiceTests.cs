using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>Issue #301: the advance against a FAKE store and a FAKE host - the completion
///     that starts the next session, the refusal for a foreign step, and the claim bookkeeping
///     a failed launch must not leak. No database, no tab, no host process.</summary>
public class ChainAdvanceServiceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeStore : IChainStore
  {
    private readonly Dictionary<int, Chain> _chains = [];
    private int _next = 1;

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
    {
      Chain saved = draft with { Id = _next++, Version = 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(_chains.TryGetValue(id, out Chain? chain)
            ? Result.Success(chain)
            : Result.Failure<Chain>(new DomainError("ChainNotFound", $"no chain #{id}")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog(
            [.. _chains.Values.Where(c => status is null || c.Status == status)], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
    {
      if (!_chains.TryGetValue(chain.Id, out Chain? current))
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "gone")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("VersionConflict", "stale")));
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  /// <summary>A store that belongs to the session's container and dies with it (issue #340):
  ///     once the host closes the session, every later call faults exactly as a disposed
  ///     provider does. This is the shape the app has - the chain store is resolved from the
  ///     session's own container - so a service that writes after closing cannot be correct.
  ///     </summary>
  private sealed class ContainerOwnedStore : IChainStore
  {
    private readonly FakeStore _inner = new();

    /// <summary>Set by the host's close, which is what disposes the container.</summary>
    public bool Disposed { get; set; }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Disposed, "IServiceProvider");

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
    {
      ThrowIfDisposed();
      return _inner.CreateAsync(draft, ct);
    }

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
    {
      ThrowIfDisposed();
      return _inner.GetAsync(id, ct);
    }

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
    {
      ThrowIfDisposed();
      return _inner.ListAsync(status, ct);
    }

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
    {
      ThrowIfDisposed();
      return _inner.SaveAsync(chain, expectedVersion, ct);
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
    {
      ThrowIfDisposed();
      return _inner.FindBySessionAsync(sessionId, ct);
    }
  }

  private sealed class FakeHost : IChainAdvanceHost
  {
    public List<string> Closed { get; } = [];

    public List<ChainStepAssignment> Started { get; } = [];

    public List<string> FinalStarted { get; } = [];

    public Result<string>? StartResult { get; set; }

    /// <summary>Runs when the host closes a session - the moment a real container is disposed.
    ///     </summary>
    public Action? OnClose { get; set; }

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
      Closed.Add(sessionId);
      OnClose?.Invoke();
      return Task.CompletedTask;
    }

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(StartResult ?? Result.Success("session-next"));
    }

    public Task<Result<string>> StartFinalSessionAsync(Chain chain, string predecessorSessionId, CancellationToken ct = default)
    {
      FinalStarted.Add(predecessorSessionId);
      return Task.FromResult(Result.Success("session-final"));
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static async Task<(ChainAdvanceService Service, FakeStore Store, FakeHost Host, Chain Chain)>
      NewAsync(params int[] positions)
  {
    FakeStore store = new();
    FakeHost host = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", positions.Length == 0 ? [1] : positions), T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    return (new ChainAdvanceService(chains, host), store, host, chain);
  }

  [Fact]
  public async Task OnCompletionAsync_StartsTheNextStep_WithNoModelCallDeciding()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, Chain chain) =
        await NewAsync(1, 2);
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.StartStep, decision.Kind);
    Assert.Equal(2, decision.Position);
    ChainStepAssignment assignment = Assert.Single(host.Started);
    Assert.Equal(2, assignment.Position);
    Assert.Equal(3, assignment.PlanId);
  }

  [Fact]
  public async Task OnCompletionAsync_OfTheLastStep_CompletesTheChainAndClosesTheSession()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync(1);
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
    Assert.Contains("session-a", host.Closed);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  [Fact]
  public async Task OnCompletionAsync_OfTheLastStep_StartsTheFinalSessionAndRecordsIt()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync(1);
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
    Assert.Contains("session-a", host.FinalStarted);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal("session-final", stored.FinalSessionId);
  }

  /// <summary>THE CLOSE MUST NOT PRECEDE THE CHAIN WRITE (issue #340). The chain store is
  ///     resolved from the completing session's own container, so closing that session disposes
  ///     the store the advance still has to write through. Before this test the close ran first
  ///     and the completion then faulted with 'Cannot access a disposed object. Object name:
  ///     IServiceProvider' - the chain was left Running with every step Done and nothing able to
  ///     move it.
  ///     <para>THE PREDICTION: with the close ordering fixed, the store is still alive for the
  ///     status write and the chain reaches Completed.</para></summary>
  [Fact]
  public async Task OnCompletionAsync_OfTheLastStep_WritesTheChainBeforeItClosesTheSession()
  {
    ContainerOwnedStore store = new();
    FakeHost host = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", [1]), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    // Closing the session is what disposes the container the store lives in.
    host.OnClose = () => store.Disposed = true;
    ChainAdvanceService service = new(chains, host);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainAdvanceKind.CompleteChain, decision.Kind);
    Assert.Contains("session-a", host.Closed);
    store.Disposed = false; // read the record through a live store, as the app would
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken).ConfigureAwait(true));
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  [Fact]
  public async Task OnCompletionAsync_FromASessionWithNoChainStep_RefusesAndStartsNothing()
  {
    (ChainAdvanceService service, _, FakeHost host, _) = await NewAsync(1, 2);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-stranger", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceCode.ForeignStep, decision.Code);
    Assert.Empty(host.Started);
    Assert.Empty(host.Closed);
  }

  [Fact]
  public async Task OnCompletionAsync_ForADifferentPlan_RefusesAndStartsNothing()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync(1, 2);
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 99, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceCode.ForeignStep, decision.Code);
    Assert.Contains("plan #99", decision.Reason, StringComparison.Ordinal);
    Assert.Empty(host.Started);
  }

  [Fact]
  public async Task OnCompletionAsync_WhenTheLaunchFails_ReleasesTheClaimWithoutSpendingAnAttempt()
  {
    (ChainAdvanceService service, FakeStore store, FakeHost host, Chain chain) = await NewAsync(1, 2);
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);
    host.StartResult = Result.Failure<string>(new DomainError("SessionLaunchFailed", "no host"));

    ChainAdvance decision = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.Refused, decision.Kind);
    Assert.Equal("SessionLaunchFailed", decision.Code);

    // The step must be available again rather than parked behind a session that never existed,
    // and the attempt the failed launch spent must be the ONLY one.
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    ChainStep next = Assert.Single(stored.Steps, s => s.Position == 2);
    Assert.Equal(ChainStepStatus.Pending, next.Status);
    Assert.Null(next.SessionId);
    Assert.Equal(1, next.Attempts);
  }

  [Fact]
  public async Task OnCompletionAsync_ClaimsTheStepForTheRealSession_WithoutASecondAttempt()
  {
    (ChainAdvanceService service, FakeStore store, _, Chain chain) = await NewAsync(1, 2);
    _ = await store.SaveAsync(chain.ClaimStep(1, "session-a", T0), chain.Version,
        TestContext.Current.CancellationToken);

    _ = Must(await service.OnCompletionAsync("session-a", planId: 3, T0,
        TestContext.Current.CancellationToken));

    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    ChainStep next = Assert.Single(stored.Steps, s => s.Position == 2);
    Assert.Equal("session-next", next.SessionId);
    Assert.Equal(1, next.Attempts);
  }

  [Fact]
  public async Task Advance_WhenTheChainCanNoLongerAdvance_ParksItInsteadOfLeavingItRunning()
  {
    // Issue #303 criterion: a chain with a parked step and nothing left to start must be
    // PARKED, not left Running. Before this, the walker returned Blocked and nothing wrote
    // the status, so the chain sat Running forever while every surface called it in progress.
    (ChainAdvanceService service, FakeStore store, FakeHost _, Chain chain) = await NewAsync(1, 2);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken));
    _ = Must(await chains.SettleStepAsync(chain.Id, 1, ChainStepStatus.Parked,
        ChainActor.Session("session-a"), "waiting on a human", T0,
        TestContext.Current.CancellationToken));
    _ = Must(await chains.ClaimStepAsync(chain.Id, 2, "session-b", T0,
        TestContext.Current.CancellationToken));
    _ = Must(await chains.SettleStepAsync(chain.Id, 2, ChainStepStatus.Parked,
        ChainActor.Session("session-b"), "also waiting", T0,
        TestContext.Current.CancellationToken));

    ChainAdvance decision = Must(await service.AdvanceAsync(chain, 2, "session-b", T0,
        TestContext.Current.CancellationToken));

    Assert.Equal(ChainAdvanceKind.Blocked, decision.Kind);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken));
    Assert.Equal(ChainStatus.Parked, stored.Status);
    Assert.NotNull(stored.ParkReason);
    Assert.Contains("neither Done nor Skipped", stored.ParkReason, StringComparison.Ordinal);
  }
}
