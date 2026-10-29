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

  private sealed class FakeHost : IChainAdvanceHost
  {
    public List<string> Closed { get; } = [];

    public List<ChainStepAssignment> Started { get; } = [];

    public Result<string>? StartResult { get; set; }

    public Task CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
      Closed.Add(sessionId);
      return Task.CompletedTask;
    }

    public Task<Result<string>> StartSessionAsync(ChainStepAssignment assignment,
        string predecessorSessionId, CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(StartResult ?? Result.Success("session-next"));
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
    Chain chain = Must(await chains.CreateAsync(new ChainCreation("C:\\ws", 3, 1, "t", "session-root",
        positions.Length == 0 ? [1] : positions), T0, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
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
}
