using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #301 D3: a session executing a chain step may complete only THAT step, and a
///     session executing none is unaffected - the ordinary way a plan is worked. Fakes only.</summary>
public class ChainStepOwnershipGateTests
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
            : Result.Failure<Chain>(new DomainError("ChainNotFound", "gone")));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Result.Success(new ChainCatalog([.. _chains.Values], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
    {
      Chain saved = chain with { Version = current(chain.Id) + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));

    private int current(int id) => _chains[id].Version;
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }

  private static async Task<ChainStepOwnershipGate> GateForAsync(Chain chain)
  {
    FakeStore store = new();
    _ = await store.CreateAsync(chain, TestContext.Current.CancellationToken).ConfigureAwait(true);
    return new ChainStepOwnershipGate(store, new FixedClock(T0));
  }

  private static Chain NewChain(int planId, params int[] positions)
      => Chain.New("C:\\ws", planId, 1, "t", "session-root",
          positions.Length == 0 ? [1] : positions, T0);

  [Fact]
  public async Task Evaluate_ForASessionOutsideAnyChain_Permits()
  {
    ChainStepOwnershipGate gate = await GateForAsync(NewChain(3, 1, 2));

    PlanStepOwnership ownership = await gate
        .EvaluateAsync(3, 1, "session-hand-started", TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    // The ordinary way a plan is worked must keep working: a session with no chain step is
    // not narrowed by a chain it is not part of.
    Assert.True(ownership.Allowed);
    Assert.Null(ownership.Code);
  }

  [Fact]
  public async Task Evaluate_ForTheAssignedStep_Permits()
  {
    Chain chain = NewChain(3, 1, 2).ClaimStep(2, "session-a", T0);
    ChainStepOwnershipGate gate = await GateForAsync(chain);

    PlanStepOwnership ownership = await gate
        .EvaluateAsync(3, 2, "session-a", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.True(ownership.Allowed);
  }

  [Fact]
  public async Task Evaluate_ForADifferentStep_RefusesNamingTheAssignedOne()
  {
    Chain chain = NewChain(3, 1, 2).ClaimStep(2, "session-a", T0);
    ChainStepOwnershipGate gate = await GateForAsync(chain);

    PlanStepOwnership ownership = await gate
        .EvaluateAsync(3, 1, "session-a", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(ownership.Allowed);
    Assert.Equal(ChainAdvanceCode.ForeignStep, ownership.Code);
    Assert.Contains("step 2", ownership.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Evaluate_ForTheSamePositionOnAnotherPlan_Refuses()
  {
    Chain chain = NewChain(3, 1, 2).ClaimStep(2, "session-a", T0);
    ChainStepOwnershipGate gate = await GateForAsync(chain);

    // The position alone is not the assignment.
    PlanStepOwnership ownership = await gate
        .EvaluateAsync(99, 2, "session-a", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(ownership.Allowed);
    Assert.Equal(ChainAdvanceCode.ForeignStep, ownership.Code);
  }

  [Fact]
  public async Task Evaluate_UnderALapsedClaim_RefusesWithTheLapsedCode()
  {
    DateTimeOffset stale = T0 - ChainClaimPolicy.ClaimTtl - TimeSpan.FromMinutes(1);
    Chain chain = NewChain(3, 1, 2).ClaimStep(2, "session-a", stale);
    ChainStepOwnershipGate gate = await GateForAsync(chain);

    PlanStepOwnership ownership = await gate
        .EvaluateAsync(3, 2, "session-a", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(ownership.Allowed);
    Assert.Equal(ChainAdvanceCode.ClaimLapsed, ownership.Code);
    Assert.Contains("re-claim", ownership.Message, StringComparison.Ordinal);
  }
}
