using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Issue #300: the chain application service against a FAKE store — the
///     aggregate-exception to DomainError mapping, the CAS version it saves at, and the
///     events it raises. No database, no ACL: a domain test must never know SQL exists.
///     The real store's behaviour is covered by the Storage ACL's integration tests.</summary>
public class ChainServiceTests
{
  private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

  private sealed class FakeStore : IChainStore
  {
    private readonly Dictionary<int, Chain> _chains = [];
    private int _next = 1;

    public List<int> SavedAtVersion { get; } = [];

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
      SavedAtVersion.Add(expectedVersion);
      if (!_chains.TryGetValue(chain.Id, out Chain? current))
      {
        return Task.FromResult(Result.Failure<Chain>(
            new DomainError("ChainNotFound", $"no chain #{chain.Id}")));
      }

      if (current.Version != expectedVersion)
      {
        return Task.FromResult(Result.Failure<Chain>(new DomainError("VersionConflict",
            $"chain #{chain.Id} is at v{current.Version}; expectedVersion was {expectedVersion}")));
      }

      Chain saved = chain with { Version = current.Version + 1 };
      _chains[saved.Id] = saved;
      return Task.FromResult(Result.Success(saved));
    }

    public Task<Result<Chain?>> FindBySessionAsync(string sessionId, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_chains.Values.FirstOrDefault(chain => chain.Steps
            .Any(step => string.Equals(step.SessionId, sessionId, StringComparison.Ordinal)))));
  }

  private static (ChainService Service, FakeStore Store) New()
  {
    FakeStore store = new();
    return (new ChainService(store), store);
  }

  /// <summary>Asserts success and yields the value: Result.Value is only non-null on
  ///     success, and every test here reads the chain it just built.</summary>
  private static T Must<T>(Result<T> result)
  {
    Assert.True(result.IsSuccess, result.Error?.Message ?? "expected success");
    return result.Value;
  }

  private static Task<Result<Chain>> CreateChainAsync(ChainService service, params int[] positions)
      => service.CreateAsync(ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root",
          positions.Length == 0 ? [1] : positions), T0, TestContext.Current.CancellationToken);

  [Fact]
  public async Task CreateAsync_RaisesCreated_WithThePlanItExecutes()
  {
    (ChainService service, _) = New();
    List<ChainCreated> created = [];
    service.Created += created.Add;

    Result<Chain> result = await CreateChainAsync(service, 1, 2);

    Assert.True(result.IsSuccess, result.Error?.Message ?? "create must succeed");
    ChainCreated evt = Assert.Single(created);
    Assert.Equal(3, evt.PlanId);
    Assert.Equal(1, evt.PlanVersion);
    Assert.Equal("session-root", evt.SessionId);
  }

  [Fact]
  public async Task CreateAsync_WithNoSteps_MapsTheAggregateRefusalToInvalidTransition()
  {
    (ChainService service, _) = New();
    List<ChainCreated> created = [];
    service.Created += created.Add;

    // The helper defaults to one position; the empty case is stated explicitly here
    // because 'no executable step' is exactly what this test is about.
    Result<Chain> result = await service.CreateAsync(
        ChainCreation.Implementing("C:\\ws", 3, 1, "t", "session-root", []), T0,
        TestContext.Current.CancellationToken);

    Assert.False(result.IsSuccess);
    Assert.Equal("InvalidTransition", result.Error.Code);
    Assert.Empty(created);
  }

  [Fact]
  public async Task ClaimStepAsync_SavesAtTheVersionItLoaded_AndRaisesTheStepEvent()
  {
    (ChainService service, FakeStore store) = New();
    Chain chain = Must(await CreateChainAsync(service, 1));
    List<ChainStepChanged> changed = [];
    service.StepChanged += changed.Add;

    Result<Chain> claimed = await service.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken);

    Assert.True(claimed.IsSuccess, claimed.Error?.Message ?? "claim must succeed");
    Assert.Equal([chain.Version], store.SavedAtVersion);
    ChainStepChanged evt = Assert.Single(changed);
    Assert.Equal("claimed", evt.Kind);
    Assert.Equal("session:session-a", evt.Actor.Encoded);
  }

  [Fact]
  public async Task ClaimStepAsync_OnALiveClaim_MapsToInvalidTransition_AndRaisesNothing()
  {
    (ChainService service, _) = New();
    Chain chain = Must(await CreateChainAsync(service, 1));
    _ = await service.ClaimStepAsync(chain.Id, 1, "session-a", T0,
        TestContext.Current.CancellationToken);
    List<ChainStepChanged> changed = [];
    service.StepChanged += changed.Add;

    Result<Chain> again = await service.ClaimStepAsync(chain.Id, 1, "session-b", T0,
        TestContext.Current.CancellationToken);

    Assert.False(again.IsSuccess);
    Assert.Equal("InvalidTransition", again.Error.Code);
    Assert.Empty(changed);
  }

  [Fact]
  public async Task SettleStepAsync_ReportsTheKindForEachSettledStatus()
  {
    (ChainService service, _) = New();
    Chain chain = Must(await CreateChainAsync(service, 1, 2));
    List<ChainStepChanged> changed = [];
    service.StepChanged += changed.Add;

    _ = await service.SettleStepAsync(chain.Id, 1, ChainStepStatus.Failed, ChainActor.Harness,
        "provider error", T0, TestContext.Current.CancellationToken);
    _ = await service.SettleStepAsync(chain.Id, 2, ChainStepStatus.Skipped, ChainActor.Harness,
        "not needed", T0, TestContext.Current.CancellationToken);

    Assert.Equal(["failed", "skipped"], changed.Select(c => c.Kind));
  }

  [Fact]
  public async Task ParkAsync_RaisesStatusChanged_WithTheReasonAndTheHarnessActor()
  {
    (ChainService service, _) = New();
    Chain chain = Must(await CreateChainAsync(service, 1));
    List<ChainStatusChanged> changed = [];
    service.StatusChanged += changed.Add;

    Result<Chain> parked = await service.ParkAsync(chain.Id, "gate failed twice", 1,
        ChainActor.Harness, T0, TestContext.Current.CancellationToken);

    Assert.True(parked.IsSuccess, parked.Error?.Message ?? "park must succeed");
    ChainStatusChanged evt = Assert.Single(changed);
    Assert.Equal(ChainStatus.Running, evt.From);
    Assert.Equal(ChainStatus.Parked, evt.To);
    Assert.Equal("gate failed twice", evt.ParkReason);
    Assert.True(evt.Actor.IsHarness);
  }

  [Fact]
  public async Task SetStatusAsync_CompletionWithAPendingStep_RaisesNoEvent()
  {
    (ChainService service, _) = New();
    Chain chain = Must(await CreateChainAsync(service, 1, 2));
    List<ChainStatusChanged> changed = [];
    service.StatusChanged += changed.Add;

    Result<Chain> completed = await service.SetStatusAsync(chain.Id, ChainStatus.Completed,
        ChainActor.Harness, T0, TestContext.Current.CancellationToken);

    Assert.False(completed.IsSuccess);
    Assert.Equal("InvalidTransition", completed.Error.Code);
    Assert.Empty(changed);
  }

  [Fact]
  public async Task SetStatusAsync_PassesAStoreFailureThroughVerbatim()
  {
    (ChainService service, _) = New();

    Result<Chain> missing = await service.SetStatusAsync(99, ChainStatus.Abandoned,
        ChainActor.User, T0, TestContext.Current.CancellationToken);

    Assert.False(missing.IsSuccess);
    Assert.Equal("ChainNotFound", missing.Error.Code);
  }

  [Fact]
  public async Task ResumeAsync_Unparks_AndClearsTheParkFacts()
  {
    (ChainService service, _) = New();
    Chain chain = Must(await CreateChainAsync(service, 1));
    _ = await service.ParkAsync(chain.Id, "waiting", 1, ChainActor.Harness, T0,
        TestContext.Current.CancellationToken);

    Result<Chain> resumed = await service.ResumeAsync(chain.Id, ChainActor.User, T0,
        TestContext.Current.CancellationToken);

    Assert.True(resumed.IsSuccess, resumed.Error?.Message ?? "resume must succeed");
    Assert.Equal(ChainStatus.Running, resumed.Value.Status);
    Assert.Null(resumed.Value.ParkReason);
  }
}
