using eThangAgent.AgentDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Agent.Application.Tests;

/// <summary>A stuck chain, repaired against a fake store and a fake launch: the three repairs
///     enacted, and the two refusals that keep a healthy chain from being disturbed.
///     <para>THE STATE BEING REPAIRED is a Running chain nothing can move - the shape that used
///     to require a human to read the record and work out what happened.</para></summary>
public class ChainRepairServiceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

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

    public Task<Result<Chain>> SaveAsync(Chain chain, int expectedVersion,
        CancellationToken ct = default)
    {
      if (!_chains.TryGetValue(chain.Id, out Chain? current))
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("ChainNotFound", "gone")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Chain>(
            new DomainError("VersionConflict", "stale")));
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  private sealed class FakeLaunch : IChainResumeLaunchPort
  {
    public List<ChainStepAssignment> Started { get; } = [];

    public Result<string>? Outcome { get; set; }

    public Task<Result<string>> StartResumedSessionAsync(ChainStepAssignment assignment,
        CancellationToken ct = default)
    {
      Started.Add(assignment);
      return Task.FromResult(Outcome ?? Result.Success("session-repaired"));
    }
  }

  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static async Task<(ChainRepairService Service, FakeStore Store, FakeLaunch Launch,
      Chain Chain)> NewAsync(params int[] positions)
  {
    FakeStore store = new();
    FakeLaunch launch = new();
    ChainService chains = new(store);
    Chain chain = Must(await chains.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t",
        "session-root", positions), T0, TestContext.Current.CancellationToken).ConfigureAwait(true));
    return (new ChainRepairService(chains, launch), store, launch, chain);
  }

  private static async Task<Chain> SettleAsync(ChainService chains, Chain chain, int position,
      ChainStepStatus status)
  {
    _ = Must(await chains.ClaimStepAsync(chain.Id, position, "session-" + position, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
    return Must(await chains.SettleStepAsync(chain.Id, position, status,
        ChainActor.Session("session-" + position), null, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));
  }

  [Fact]
  public async Task Repair_StartsTheAvailableStep_AndClaimsItForTheNewSession()
  {
    // THE ADVANCE NEVER STARTED STEP 2: step 1 settled, and nothing holds what comes next.
    (ChainRepairService service, FakeStore store, FakeLaunch launch, Chain chain) =
        await NewAsync(1, 2).ConfigureAwait(true);
    ChainService chains = new(store);
    _ = await SettleAsync(chains, chain, 1, ChainStepStatus.Done).ConfigureAwait(true);

    ChainRepairOutcome outcome = Must(await service.RepairAsync(chain.Id, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainRepairKind.StartStep, outcome.Kind);
    Assert.Equal(2, outcome.Position);
    Assert.Equal("session-repaired", outcome.SessionId);
    Assert.Equal(2, Assert.Single(launch.Started).Position);
    // THE CLAIM IS CORRECTED TO THE REAL SESSION, so the new session can complete the step.
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Running, stored.StepAt(2)!.Status);
    Assert.Equal("session-repaired", stored.StepAt(2)!.SessionId);
  }

  [Fact]
  public async Task Repair_ReleasesTheClaim_WhenTheLaunchFails()
  {
    // A LAUNCH THAT FAILS MUST NOT LEAVE THE STEP HELD by a session that never existed: the
    // step goes back to available so the next attempt can start it.
    (ChainRepairService service, FakeStore store, FakeLaunch launch, Chain chain) =
        await NewAsync(1, 2).ConfigureAwait(true);
    ChainService chains = new(store);
    _ = await SettleAsync(chains, chain, 1, ChainStepStatus.Done).ConfigureAwait(true);
    launch.Outcome = Result.Failure<string>(new DomainError("TabClosed", "no tab"));

    Result<ChainRepairOutcome> outcome = await service.RepairAsync(chain.Id, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStepStatus.Pending, stored.StepAt(2)!.Status);
    Assert.Null(stored.StepAt(2)!.SessionId);
  }

  [Fact]
  public async Task Repair_CompletesAChainWhoseEveryStepSettled()
  {
    (ChainRepairService service, FakeStore store, _, Chain chain) =
        await NewAsync(1).ConfigureAwait(true);
    ChainService chains = new(store);
    _ = await SettleAsync(chains, chain, 1, ChainStepStatus.Done).ConfigureAwait(true);

    ChainRepairOutcome outcome = Must(await service.RepairAsync(chain.Id, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainRepairKind.CompleteChain, outcome.Kind);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Completed, stored.Status);
  }

  [Fact]
  public async Task Repair_ParksAChainBlockedOnAFailedStep()
  {
    (ChainRepairService service, FakeStore store, FakeLaunch launch, Chain chain) =
        await NewAsync(1, 2).ConfigureAwait(true);
    ChainService chains = new(store);
    _ = await SettleAsync(chains, chain, 1, ChainStepStatus.Failed).ConfigureAwait(true);

    ChainRepairOutcome outcome = Must(await service.RepairAsync(chain.Id, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Assert.Equal(ChainRepairKind.ParkChain, outcome.Kind);
    Assert.Equal(1, outcome.Position);
    Assert.Empty(launch.Started);
    Chain stored = Must(await store.GetAsync(chain.Id, TestContext.Current.CancellationToken)
        .ConfigureAwait(true));
    Assert.Equal(ChainStatus.Parked, stored.Status);
    Assert.Equal(1, stored.ParkedAtPosition);
  }

  [Fact]
  public async Task Repair_RefusesAChainThatIsAlreadyMoving_NamingTheStep()
  {
    // A LIVE SESSION HOLDS THE WORK. Repairing would start a second session for it.
    (ChainRepairService service, FakeStore store, FakeLaunch launch, Chain chain) =
        await NewAsync(1, 2).ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Result<ChainRepairOutcome> outcome = await service.RepairAsync(chain.Id, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Assert.Equal(ChainRepairCode.AlreadyMoving, outcome.Error.Code);
    Assert.Contains("step(s) 1", outcome.Error.Message, StringComparison.Ordinal);
    Assert.Empty(launch.Started);
  }

  [Fact]
  public async Task Repair_RefusesAParkedChain_ByName()
  {
    (ChainRepairService service, FakeStore store, _, Chain chain) =
        await NewAsync(1, 2).ConfigureAwait(true);
    ChainService chains = new(store);
    _ = Must(await chains.ParkAsync(chain.Id, "a human is looking", 1, ChainActor.Harness, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true));

    Result<ChainRepairOutcome> outcome = await service.RepairAsync(chain.Id, T0,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(outcome.IsSuccess);
    Assert.Equal(ChainRepairCode.NotRunning, outcome.Error.Code);
    // THE REFUSAL NAMES THE ACTION THAT DOES APPLY, so a caller is not left guessing.
    Assert.Contains("resume-parked", outcome.Error.Message, StringComparison.Ordinal);
  }
}
