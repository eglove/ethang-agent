using eThangAgent.AgentDomain;
using eThangAgent.PlanDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Composition.Tests;

/// <summary>The structure lock's answer, read from the chain record (2026-11-16): only a RUNNING
///     chain holds a plan's positions, a chain-store fault is not a lock, and the answer carries
///     the FURTHEST position the chain holds so an append past it can be told from an insert
///     inside it. Fakes only.</summary>
public class ChainPlanExecutionLockTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeStore(params Chain[] chains) : IChainStore
  {
    public bool Faulted { get; init; }

    public Task<Result<Chain>> CreateAsync(Chain draft, CancellationToken ct = default)
        => throw new NotSupportedException("the lock never writes.");

    public Task<Result<Chain>> GetAsync(int id, CancellationToken ct = default)
        => Task.FromResult(Result.Success(chains.First(chain => chain.Id == id)));

    public Task<Result<ChainCatalog>> ListAsync(ChainStatus? status, CancellationToken ct = default)
        => Task.FromResult(Faulted
            ? Result.Failure<ChainCatalog>(new DomainError("StorageDown", "the chain store is unavailable."))
            : Result.Success(new ChainCatalog(
                [.. chains.Where(chain => status is null || chain.Status == status)], [])));

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion, CancellationToken ct = default)
        => throw new NotSupportedException("the lock never writes.");

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success<Chain?>(null));
  }

  private static Chain ChainFor(int id, int planId, ChainStatus status, int lastPosition = 2)
  {
    Chain chain = Chain.New("C:\\ws", planId, 1, "t", "session-root",
        [.. Enumerable.Range(1, lastPosition)], T0) with
    { Id = id };
    return status switch
    {
      ChainStatus.Parked => chain.Park("waiting for a human", 1, ChainActor.User, T0),
      ChainStatus.Running => chain,
      ChainStatus.Completed or ChainStatus.Abandoned => chain with { Status = status },
      _ => throw new ArgumentOutOfRangeException(nameof(status), status, "not a chain status."),
    };
  }

  [Fact]
  public async Task ARunningChainOverThePlan_IsTheLock_AndNamesItsFurthestPosition()
  {
    ChainPlanExecutionLock planLock = new(new FakeStore(
        ChainFor(7, planId: 42, ChainStatus.Running, lastPosition: 3)));

    PlanExecutionLock? held = await planLock.ExecutionAsync(42, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    Assert.NotNull(held);
    Assert.Equal(7, held.ChainId);
    Assert.Equal(3, held.LastPosition);
  }

  [Fact]
  public async Task ARunningChainOverADifferentPlan_LocksNothing()
  {
    ChainPlanExecutionLock planLock = new(new FakeStore(ChainFor(7, planId: 42, ChainStatus.Running)));

    Assert.Null(await planLock.ExecutionAsync(99, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
  }

  [Fact]
  public async Task AParkedChain_LocksNothing()
  {
    // Parked waits for a human, who may abandon or resume it; the plan is theirs to fix.
    ChainPlanExecutionLock planLock = new(new FakeStore(ChainFor(7, planId: 42, ChainStatus.Parked)));

    Assert.Null(await planLock.ExecutionAsync(42, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
  }

  [Fact]
  public async Task ACompletedOrAbandonedChain_LocksNothing()
  {
    ChainPlanExecutionLock completed = new(new FakeStore(ChainFor(7, 42, ChainStatus.Completed)));
    ChainPlanExecutionLock abandoned = new(new FakeStore(ChainFor(8, 42, ChainStatus.Abandoned)));

    Assert.Null(await completed.ExecutionAsync(42, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Null(await abandoned.ExecutionAsync(42, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
  }

  [Fact]
  public async Task AnUnreadableChainStore_IsNotALock()
  {
    // A fault the caller cannot see must not make every plan in the workspace uneditable.
    ChainPlanExecutionLock planLock = new(new FakeStore { Faulted = true });

    Assert.Null(await planLock.ExecutionAsync(42, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
  }
}
